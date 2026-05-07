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

    private readonly string userUrl;
    private readonly Process ytdlp;
    private readonly StreamReader ytdlpStdout;
    private readonly BinaryManager binaries;
    private readonly CancellationTokenSource cts = new();

    private SubprocessAudioReader? currentInner;
    private Task<SubprocessAudioReader?>? advanceTask;
    private bool exhausted;
    private bool exhaustedNaturally;
    private bool killedByUs;
    private bool disposed;

    private PlaylistAudioReader(string userUrl, Process ytdlp, BinaryManager binaries, SubprocessAudioReader firstInner)
    {
        this.userUrl = userUrl;
        this.ytdlp = ytdlp;
        this.ytdlpStdout = ytdlp.StandardOutput;
        this.binaries = binaries;
        this.currentInner = firstInner;
    }

    public static async Task<PlaylistAudioReader> CreateAsync(
        string url, BinaryManager binaries, bool playlistRandom = false,
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
        // Title first, URL second — same yt-dlp invocation, so no cold-start
        // penalty. Each item emits two stdout lines (title, then URL); we
        // read them in pairs here and in AdvanceAsync. Lets the Now Playing
        // header show "Song Title" instead of a googlevideo CDN URL.
        psi.ArgumentList.Add("--print"); psi.ArgumentList.Add("%(title)s");
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("bestaudio/best");
        psi.ArgumentList.Add("--no-playlist");
        // yt-dlp emits one URL per item to stdout; we consume them lazily
        // line-by-line as ffmpeg songs end. With --playlist-random, yt-dlp
        // shuffles the playlist before emitting.
        if (playlistRandom)
            psi.ArgumentList.Add("--playlist-random");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add(url);

        var ytdlp = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp");

        // Read first item (title + URL) synchronously so the caller gets a
        // playable reader immediately. Bound the wait so a stuck yt-dlp
        // doesn't hang us.
        using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initCts.CancelAfter(TimeSpan.FromSeconds(20));
        string? titleLine;
        string? urlLine;
        try
        {
            titleLine = await ytdlp.StandardOutput.ReadLineAsync(initCts.Token);
            urlLine = await ytdlp.StandardOutput.ReadLineAsync(initCts.Token);
        }
        catch
        {
            try { ytdlp.Kill(entireProcessTree: true); } catch { }
            try { ytdlp.Dispose(); } catch { }
            throw;
        }

        if (string.IsNullOrEmpty(urlLine))
        {
            // yt-dlp closed stdout without emitting a URL — bubble the
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

        if (!string.IsNullOrWhiteSpace(titleLine))
            TitleCache.Set(url, titleLine.Trim());

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

        var firstInner = SubprocessAudioReader.FromResolvedUrl(urlLine.Trim(), binaries, ct);
        return new PlaylistAudioReader(url, ytdlp, binaries, firstInner);
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
            // Each item is two stdout lines (title, then URL) — match the
            // CreateAsync init read. Title-empty is fine (some extractors
            // omit it); URL-empty means the playlist is exhausted.
            var titleLine = await ytdlpStdout.ReadLineAsync(cts.Token);
            if (titleLine == null) return null;       // EOF on title line
            var urlLine = await ytdlpStdout.ReadLineAsync(cts.Token);
            if (string.IsNullOrEmpty(urlLine)) return null;

            // For multi-item playlists the title changes per song, so the
            // cache entry under the user's URL gets overwritten as items
            // play through. With --no-playlist (current default) this only
            // runs once at most before EOF.
            if (!string.IsNullOrWhiteSpace(titleLine))
                TitleCache.Set(userUrl, titleLine.Trim());

            return SubprocessAudioReader.FromResolvedUrl(urlLine.Trim(), binaries, cts.Token);
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
