using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Plays a yt-dlp-resolvable URL (single video OR playlist) by holding a
/// long-lived yt-dlp process that emits item URLs lazily and chaining a
/// sequence of <see cref="SubprocessAudioReader"/> ffmpegs as items end.
///
/// Lifecycle:
///   • Construction spawns yt-dlp with --lazy-playlist (or --playlist-random)
///     and reads the first emitted URL synchronously, so the caller gets a
///     ready-to-play reader in one shot.
///   • Read() forwards to the current ffmpeg-backed inner. When the inner
///     EOFs, an async advance task reads the next URL from yt-dlp's stdout
///     and spawns the next ffmpeg. Read returns silence to bridge the gap.
///   • When yt-dlp's stdout closes (no more items), Read returns 0 — the
///     wrapper's "natural EOF" signal. <see cref="StreamPlayer"/> propagates
///     that through PlaybackStopped, and the existing Plugin loop logic
///     decides whether to restart the playlist (loop on) or stop (loop off).
///
/// Single videos: yt-dlp emits one URL and exits → one inner ffmpeg → its
/// EOF flows through as natural EOF. The Plugin loop replays the URL,
/// which respawns this whole wrapper from scratch.
/// </summary>
internal sealed class PlaylistAudioReader : ISampleProvider, IDisposable, ICleanExitSource, ISeekableSource, ISkippableSource
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly Process ytdlp;
    private readonly StreamReader ytdlpStdout;
    private readonly BinaryManager binaries;
    // Original user-provided URL (the playlist URL). Per-item titles emitted
    // by yt-dlp during AdvanceAsync are cached under this key, so the Now
    // Playing header — which reads TitleCache by user URL — picks up the
    // currently-playing track as the playlist progresses.
    private readonly string userUrl;
    private readonly CancellationTokenSource cts = new();

    private SubprocessAudioReader? currentInner;
    private Task<SubprocessAudioReader?>? advanceTask;
    // Guards advanceTask writes from racing the audio thread (Read) against
    // the Process.Exited threadpool callback (OnInnerNaturalExited). Held only
    // long enough to test-and-set; never wraps user code.
    private readonly object advanceLock = new();
    private bool exhausted;
    private bool exhaustedNaturally;
    private bool killedByUs;
    private bool disposed;
    // One-shot guard so we don't log "returning silence" on every audio thread
    // Read during an inter-item gap (hundreds of times per second). Reset
    // when a new inner is installed.
    private bool silenceGapLogged;

    private PlaylistAudioReader(
        Process ytdlp, BinaryManager binaries, string userUrl,
        SubprocessAudioReader firstInner)
    {
        this.ytdlp = ytdlp;
        this.ytdlpStdout = ytdlp.StandardOutput;
        this.binaries = binaries;
        this.userUrl = userUrl;
        this.currentInner = firstInner;
    }

    public static async Task<PlaylistAudioReader> CreateAsync(
        string url, BinaryManager binaries, bool playlistRandom = false,
        string? cookiesFromBrowser = null,
        CancellationToken ct = default)
    {
        if (!binaries.Ready)
            throw new InvalidOperationException("ffmpeg/yt-dlp not yet installed");

        // Long-form path: yt-dlp emits one resolved URL per playlist item
        // lazily; we consume them line-by-line as ffmpeg songs end. The
        // arg shape matches SubprocessAudioReader.ResolveUrlAsync (single-shot).
        var psi = binaries.BuildYtDlpStartInfo(url, playlistRandom, cookiesFromBrowser);

        var ytdlp = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp");

        // Read first URL synchronously so the caller gets a playable reader
        // immediately. Bound the wait so a stuck yt-dlp doesn't hang us.
        using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initCts.CancelAfter(TimeSpan.FromSeconds(20));
        string? firstLine;
        try
        {
            firstLine = await ytdlp.StandardOutput.ReadLineAsync(initCts.Token);
        }
        catch
        {
            try { ytdlp.Kill(entireProcessTree: true); } catch { }
            try { ytdlp.Dispose(); } catch { }
            throw;
        }

        if (string.IsNullOrEmpty(firstLine))
        {
            // yt-dlp closed stdout without emitting anything — bubble the
            // stderr message so the user sees why (private playlist, etc.).
            string stderr;
            try
            {
                await ytdlp.WaitForExitAsync(initCts.Token);
                stderr = (await ytdlp.StandardError.ReadToEndAsync()).Trim();
            }
            catch { stderr = ""; }
            try { ytdlp.Dispose(); } catch { }
            throw new InvalidOperationException(
                string.IsNullOrEmpty(stderr) ? "yt-dlp returned no URL" : $"yt-dlp: {stderr}");
        }

        // Drain stderr in the background so the pipe doesn't fill and stall
        // yt-dlp once we start consuming stdout lazily.
        _ = ProcessLog.DrainStderrInBackground(ytdlp, "yt-dlp");

        var firstResolvedUrl = YtDlpDisplayTitle.ParseAndCache(firstLine, url);
        if (firstResolvedUrl == null)
        {
            try { ytdlp.Kill(entireProcessTree: true); } catch { }
            try { ytdlp.Dispose(); } catch { }
            throw new InvalidOperationException("yt-dlp: empty URL");
        }

        var firstInner = SubprocessAudioReader.FromResolvedUrl(firstResolvedUrl, binaries, ct);
        var pl = new PlaylistAudioReader(ytdlp, binaries, url, firstInner);
        // Hook the natural-exit signal so the next item's spawn starts in
        // parallel with this one's pipe + WaveOut buffer draining (~400-500ms
        // head-start that we'd otherwise miss between songs).
        firstInner.NaturalExited += pl.OnInnerNaturalExited;
        return pl;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (disposed || exhausted) return 0;

        // Active inner: forward to its Read.
        if (currentInner != null)
        {
            var n = currentInner.Read(buffer, offset, count);
            if (n > 0) return n;
            // Inner EOF — kick off an async advance to the next URL.
            // (Often a no-op now: NaturalExited fires when ffmpeg actually
            // exits, ~400-500ms before Read sees 0, so advance is usually
            // already running by the time we get here.)
            Plugin.Log.Info("[playlist] inner EOF on audio thread — kicking advance");
            currentInner.Dispose();
            currentInner = null;
            StartAdvanceIfNeeded();
        }

        // Awaiting next inner.
        if (advanceTask != null)
        {
            if (advanceTask.IsCompleted)
            {
                SubprocessAudioReader? next = null;
                try { next = advanceTask.Result; }
                catch { next = null; }
                advanceTask = null;

                if (next != null)
                {
                    Plugin.Log.Info("[playlist] next inner installed; resuming reads");
                    silenceGapLogged = false;
                    currentInner = next;
                    return Read(buffer, offset, count);
                }

                // No next song — playlist ended. Distinguish "user killed
                // us" from "yt-dlp exhausted naturally" so the natural-end
                // event fires only when appropriate.
                Plugin.Log.Info(
                    $"[playlist] exhausted (killedByUs={killedByUs}); returning EOF");
                exhausted = true;
                if (!killedByUs) exhaustedNaturally = true;
                return 0;
            }

            // Still resolving — return silence to bridge the inter-song gap.
            // NAudio keeps the WaveOut alive instead of treating this as EOF.
            if (!silenceGapLogged)
            {
                silenceGapLogged = true;
                Plugin.Log.Info("[playlist] returning silence — awaiting next inner");
            }
            Array.Clear(buffer, offset, count);
            return count;
        }

        return 0;
    }

    private async Task<SubprocessAudioReader?> AdvanceAsync()
    {
        Plugin.Log.Info("[playlist] advance: awaiting next URL from yt-dlp");
        try
        {
            var line = await ytdlpStdout.ReadLineAsync(cts.Token);
            if (string.IsNullOrEmpty(line))
            {
                Plugin.Log.Info("[playlist] advance: yt-dlp stdout closed (playlist exhausted)");
                return null;
            }
            // Refreshes the Now Playing label for the playlist's user URL
            // before the next ffmpeg starts, so the header flips to the
            // new track at the same time playback does (modulo a small
            // ffmpeg-spawn gap). Empty label inside ParseAndCache leaves
            // the previous one in place — fine, the URL fallback only
            // kicks in if no item ever populated a label.
            var resolvedUrl = YtDlpDisplayTitle.ParseAndCache(line, userUrl);
            if (resolvedUrl == null)
            {
                Plugin.Log.Warning("[playlist] advance: yt-dlp emitted unparseable line, ending");
                return null;
            }
            Plugin.Log.Info("[playlist] advance: spawning next inner ffmpeg");
            var inner = SubprocessAudioReader.FromResolvedUrl(resolvedUrl, binaries, cts.Token);
            inner.NaturalExited += OnInnerNaturalExited;
            return inner;
        }
        catch (OperationCanceledException)
        {
            Plugin.Log.Info("[playlist] advance: cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[playlist] advance failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Subscribed to each inner's <see cref="SubprocessAudioReader.NaturalExited"/>.
    /// Fires on a threadpool thread (Process.Exited callback) ~400-500ms before
    /// the audio thread's Read sees 0, because the OS pipe + WaveOut device
    /// buffer still hold playable audio. Pre-starts the next item's spawn so
    /// its ffmpeg cold-start (HTTP connect, first segment, first decoded
    /// samples) overlaps the drain instead of running sequentially after it.
    /// </summary>
    private void OnInnerNaturalExited() => StartAdvanceIfNeeded();

    /// <summary>
    /// Idempotent start for <see cref="advanceTask"/>. Called from both the
    /// audio thread (Read's EOF path) and the Process.Exited callback; the
    /// lock makes the test-and-set race-free so the second caller becomes a
    /// no-op rather than overwriting an in-flight advance.
    /// </summary>
    private void StartAdvanceIfNeeded()
    {
        lock (advanceLock)
        {
            if (advanceTask != null) return;
            if (disposed || exhausted) return;
            advanceTask = Task.Run(AdvanceAsync);
        }
    }

    public bool DidExitCleanly() => exhaustedNaturally && !killedByUs;

    /// <summary>
    /// Position within the currently-playing item. Resets to zero each time
    /// the playlist advances — the seek bar represents "where am I in this
    /// song", not "how far through the whole playlist". Returns 0 during
    /// the inter-item gap (no current inner) and after natural exhaustion.
    /// </summary>
    public double PositionSeconds => currentInner?.PositionSeconds ?? 0;

    /// <summary>
    /// Seek inside the currently-playing item. Forwarded to the current
    /// inner ffmpeg-backed reader; no-op while the playlist is between
    /// items (Read returning silence) since there's no playhead to move.
    /// </summary>
    public void SeekToSeconds(double seconds)
    {
        currentInner?.SeekToSeconds(seconds);
    }

    /// <summary>
    /// Jump to the next playlist item by disposing the current inner
    /// ffmpeg. The Read loop's existing EOF detection sees stdout closed,
    /// kicks off AdvanceAsync, which pulls the next URL from yt-dlp and
    /// spawns a fresh ffmpeg — exactly the same flow as a natural item
    /// end, just triggered on demand. For single-video URLs (1-item
    /// playlist) this exhausts the wrapper, and the host's PlaybackStopped
    /// → natural-end loop logic handles the next behavior (restart on
    /// loop-on, stop on loop-off). Safe from any thread; SubprocessAudio
    /// Reader.Dispose is idempotent and concurrent-safe.
    /// </summary>
    public void SkipToNext()
    {
        Plugin.Log.Info(
            $"[playlist] SkipToNext called. hasInner={currentInner != null} " +
            $"advancePending={advanceTask != null} exhausted={exhausted}");
        currentInner?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Only flag "killed by us" if we're tearing down mid-stream. If the
        // playlist already exhausted naturally, a late teardown (e.g. the
        // framework auto-restart racing PlaybackStopped's deferred handler)
        // must not retroactively flip DidExitCleanly to false — that's the
        // signal the loop logic depends on.
        if (!exhausted) killedByUs = true;
        try { cts.Cancel(); } catch { }
        try { currentInner?.Dispose(); } catch { }
        try
        {
            if (!ytdlp.HasExited) ytdlp.Kill(entireProcessTree: true);
        }
        catch { }
        try { ytdlp.Dispose(); } catch { }
        try { cts.Dispose(); } catch { }
    }
}
