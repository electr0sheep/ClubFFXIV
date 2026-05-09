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
internal sealed class PlaylistAudioReader : ISampleProvider, IDisposable, ICleanExitSource
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
    private bool exhausted;
    private bool exhaustedNaturally;
    private bool killedByUs;
    private bool disposed;

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

        var psi = new ProcessStartInfo
        {
            FileName = binaries.YtDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Combined URL+metadata template (replaces -g). Each playlist item
        // emits one line containing the resolved URL and yt-dlp's metadata
        // fields, parsed by YtDlpDisplayTitle on each lazy advance.
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add(YtDlpDisplayTitle.PrintTemplate);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("bestaudio/best");
        psi.ArgumentList.Add("--no-playlist");
        // Point yt-dlp at our bundled Deno binary so it can solve YouTube's
        // signature/n-challenge JS. Without a JS runtime, yt-dlp falls back
        // to "Only images are available" for most YouTube videos.
        psi.ArgumentList.Add("--js-runtimes");
        psi.ArgumentList.Add($"deno:{binaries.DenoPath}");
        // yt-dlp emits one URL per item to stdout; we consume them lazily
        // line-by-line as ffmpeg songs end. With --playlist-random, yt-dlp
        // shuffles the playlist before emitting.
        if (playlistRandom)
            psi.ArgumentList.Add("--playlist-random");
        // Authenticate as the user's logged-in browser session — the
        // standard fix for YouTube's "Sign in to confirm you're not a bot"
        // screen. Empty leaves yt-dlp anonymous. Firefox is the
        // recommended browser; Chromium-based browsers (Chrome, Edge,
        // Brave, etc.) encrypt cookies in a way yt-dlp can't decrypt.
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookiesFromBrowser);
        }
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add(url);

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
        _ = Task.Run(async () =>
        {
            try
            {
                while (await ytdlp.StandardError.ReadLineAsync() is { } line)
                    Plugin.Log.Info($"[yt-dlp] {line}");
            }
            catch { /* exited or disposed */ }
        });

        var (firstResolvedUrl, firstLabel) = YtDlpDisplayTitle.Parse(firstLine.Trim());
        if (string.IsNullOrEmpty(firstResolvedUrl))
        {
            try { ytdlp.Kill(entireProcessTree: true); } catch { }
            try { ytdlp.Dispose(); } catch { }
            throw new InvalidOperationException("yt-dlp: empty URL");
        }
        if (!string.IsNullOrEmpty(firstLabel))
            TitleCache.Set(url, firstLabel);

        var firstInner = SubprocessAudioReader.FromResolvedUrl(firstResolvedUrl, binaries, ct);
        return new PlaylistAudioReader(ytdlp, binaries, url, firstInner);
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
            currentInner.Dispose();
            currentInner = null;
            advanceTask = Task.Run(AdvanceAsync);
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
                    currentInner = next;
                    return Read(buffer, offset, count);
                }

                // No next song — playlist ended. Distinguish "user killed
                // us" from "yt-dlp exhausted naturally" so the natural-end
                // event fires only when appropriate.
                exhausted = true;
                if (!killedByUs) exhaustedNaturally = true;
                return 0;
            }

            // Still resolving — return silence to bridge the inter-song gap.
            // NAudio keeps the WaveOut alive instead of treating this as EOF.
            Array.Clear(buffer, offset, count);
            return count;
        }

        return 0;
    }

    private async Task<SubprocessAudioReader?> AdvanceAsync()
    {
        try
        {
            var line = await ytdlpStdout.ReadLineAsync(cts.Token);
            if (string.IsNullOrEmpty(line)) return null; // exhausted
            var (resolvedUrl, label) = YtDlpDisplayTitle.Parse(line.Trim());
            if (string.IsNullOrEmpty(resolvedUrl)) return null;
            // Refresh the Now Playing label for the playlist's user URL
            // before the next ffmpeg starts, so the header flips to the
            // new track at the same time playback does (modulo a small
            // ffmpeg-spawn gap). Empty label leaves the previous one in
            // place, which is fine — the URL fallback only kicks in if no
            // item ever populated a label.
            if (!string.IsNullOrEmpty(label))
                TitleCache.Set(userUrl, label);
            return SubprocessAudioReader.FromResolvedUrl(resolvedUrl, binaries, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[playlist] advance failed: {ex.Message}");
            return null;
        }
    }

    public bool DidExitCleanly() => exhaustedNaturally && !killedByUs;

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
