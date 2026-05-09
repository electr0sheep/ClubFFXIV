using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Audio source backed by an ffmpeg subprocess that reads from a yt-dlp-resolved
/// stream URL. Outputs raw 32-bit float PCM at 44.1 kHz stereo on its stdout,
/// which we expose as an NAudio ISampleProvider.
///
/// Pipeline:
///   yt-dlp -g <url>          → resolves to direct HLS / progressive URL
///   ffmpeg -i &lt;hls&gt; ... -f f32le pipe:1   → raw PCM samples
///
/// Stops the subprocess on Dispose. Network/decoder errors return 0 from Read,
/// which signals end-of-stream to NAudio's WaveOutEvent — playback stops cleanly.
/// </summary>
public sealed class SubprocessAudioReader : ISampleProvider, IDisposable, ICleanExitSource
{
    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly Process ffmpeg;
    private readonly Stream stdout;
    private byte[] readBuffer = new byte[8192];
    private bool disposed;
    // True only when our Dispose actively killed a still-running process.
    // Stays false when Dispose runs *after* ffmpeg already exited on its own —
    // so the stderr drain can correctly attribute unexpected exits.
    private bool killedByUs;
    private bool eofLogged; // suppress repeated EOF logs from the same dead reader

    private SubprocessAudioReader(Process ffmpeg)
    {
        this.ffmpeg = ffmpeg;
        this.stdout = ffmpeg.StandardOutput.BaseStream;
    }

    /// <summary>
    /// Resolves the URL via yt-dlp, then spawns ffmpeg to decode the resolved
    /// stream into raw PCM. Throws on yt-dlp failure (offline channel, bad URL).
    /// For playlist URLs, <paramref name="playlistRandom"/> picks shuffle vs.
    /// lazy-iterate mode (the two are mutually exclusive in yt-dlp). Both modes
    /// yield the first emitted item; single-video URLs ignore both flags.
    /// </summary>
    public static async Task<SubprocessAudioReader> CreateAsync(
        string url, BinaryManager binaries, bool playlistRandom = false,
        string? cookiesFromBrowser = null,
        CancellationToken ct = default)
    {
        if (!binaries.Ready)
            throw new InvalidOperationException("ffmpeg/yt-dlp not yet installed");

        // Step 1 — let yt-dlp pick the best audio-only or low-bitrate variant
        // and print the underlying media URL.
        var resolvedUrl = await ResolveUrlAsync(url, binaries, playlistRandom, cookiesFromBrowser, ct);

        // Step 2 — ffmpeg spawn factored into FromResolvedUrl so the playlist
        // wrapper can build inner readers without re-running yt-dlp.
        return FromResolvedUrl(resolvedUrl, binaries, ct);
    }

    /// <summary>
    /// Spawn ffmpeg directly against an already-resolved media URL (no yt-dlp
    /// involvement). Used by <see cref="PlaylistAudioReader"/> after pulling
    /// the next item URL from a long-lived yt-dlp's stdout — letting the
    /// wrapper run multiple ffmpegs back-to-back from a single yt-dlp.
    /// </summary>
    public static SubprocessAudioReader FromResolvedUrl(
        string resolvedMediaUrl, BinaryManager binaries, CancellationToken ct = default)
    {
        if (!binaries.Ready)
            throw new InvalidOperationException("ffmpeg not yet installed");

        var psi = new ProcessStartInfo
        {
            FileName = binaries.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("warning");
        psi.ArgumentList.Add("-nostdin");

        // Reconnect on transient HTTP failures. Critical for Twitch HLS where
        // segment fetches can blip and the playlist URL may briefly 5xx during
        // refresh. Without these, ffmpeg exits after the first hiccup and our
        // Read returns 0 (NAudio interprets as end-of-stream).
        //
        // We deliberately do NOT use -reconnect_at_eof — it breaks YouTube live
        // by trying to re-open the input on idle gaps, which fails because the
        // URL has already advanced past the live edge.
        psi.ArgumentList.Add("-reconnect"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max"); psi.ArgumentList.Add("5");

        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(resolvedMediaUrl);
        psi.ArgumentList.Add("-vn");                 // drop video
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("pipe:1");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg");

        var reader = new SubprocessAudioReader(proc);

        // Drain stderr in the background so it doesn't fill its pipe and block ffmpeg.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync(ct) is { } line)
                    Plugin.Log.Info($"[ffmpeg] {line}");
            }
            catch { /* process exited or token cancelled */ }

            try
            {
                if (proc.HasExited && !reader.killedByUs)
                    Plugin.Log.Warning($"[ffmpeg] exited unexpectedly with code {proc.ExitCode}");
            }
            catch { /* already disposed */ }
        }, ct);

        return reader;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (disposed) return 0;
        var byteCount = count * 4; // float32 = 4 bytes
        if (readBuffer.Length < byteCount)
            readBuffer = new byte[byteCount];

        int totalBytes = 0;
        try
        {
            // ffmpeg writes in chunks; loop until we've filled the requested
            // sample count or hit EOF. Network slowness blocks here.
            while (totalBytes < byteCount)
            {
                var n = stdout.Read(readBuffer, totalBytes, byteCount - totalBytes);
                if (n == 0)
                {
                    if (totalBytes == 0 && !eofLogged)
                    {
                        eofLogged = true;
                        Plugin.Log.Warning(
                            $"[ffmpeg] stdout EOF — process exited or closed pipe. " +
                            $"HasExited={ffmpeg.HasExited} killedByUs={killedByUs}");
                    }
                    break;
                }
                totalBytes += n;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Subprocess audio read error: {ex.Message}");
            return 0;
        }

        if (totalBytes == 0) return 0;

        // Reinterpret bytes as float32 samples — endianness matches host on x64 Windows.
        var samples = MemoryMarshal.Cast<byte, float>(readBuffer.AsSpan(0, totalBytes));
        samples.CopyTo(buffer.AsSpan(offset));
        return totalBytes / 4;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!ffmpeg.HasExited)
            {
                killedByUs = true;
                ffmpeg.Kill(entireProcessTree: true);
            }
            // If ffmpeg already exited, leave killedByUs = false so the drain
            // task logs the unexpected-exit warning with whatever stderr we got.
        }
        catch { /* already gone */ }
        try { ffmpeg.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>
    /// True if ffmpeg ran to completion on its own (exit code 0) — i.e. the
    /// input stream reached a real EOF. Used by <see cref="StreamPlayer"/> to
    /// decide whether to auto-loop a finite source. Returns false if we killed
    /// the process, if it's still running (user is mid-Stop), or if it crashed
    /// with a non-zero exit code. Caller is responsible for calling this BEFORE
    /// disposing the reader; once the underlying Process is disposed, the
    /// HasExited / ExitCode lookups throw and the method conservatively returns
    /// false.
    /// </summary>
    public bool DidExitCleanly()
    {
        if (killedByUs) return false;
        try
        {
            // Don't WaitForExit here: by the time our consumer (PlaybackStopped
            // handler) reaches us, ffmpeg has already exited (its stdout EOF is
            // what caused NAudio's buffer drain to complete). Waiting would
            // only matter if the process is still running, in which case this
            // isn't a natural end anyway — return false fast.
            if (!ffmpeg.HasExited) return false;
            return ffmpeg.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ResolveUrlAsync(
        string url, BinaryManager binaries, bool playlistRandom,
        string? cookiesFromBrowser,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaries.YtDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Combined URL+metadata template (replaces -g). yt-dlp's --print
        // implies --simulate, so the format is selected and resolved but
        // not downloaded. The metadata fields drive the Now Playing label
        // (see YtDlpDisplayTitle.Parse).
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add(YtDlpDisplayTitle.PrintTemplate);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("bestaudio/best");
        // For URLs that are both a video and a playlist (e.g. /watch?v=X&list=Y),
        // --no-playlist tells yt-dlp to download the video, not the playlist.
        psi.ArgumentList.Add("--no-playlist");
        // Point yt-dlp at our bundled Deno binary so it can solve YouTube's
        // signature/n-challenge JS. Without a JS runtime, yt-dlp falls back
        // to "Only images are available" for most YouTube videos.
        psi.ArgumentList.Add("--js-runtimes");
        psi.ArgumentList.Add($"deno:{binaries.DenoPath}");
        // For pure playlist URLs, --playlist-random shuffles before emit;
        // off, yt-dlp emits items in their original order. Either way we
        // only read the first stdout line below, so we get one URL per
        // invocation. (PlaylistAudioReader is the long-form path that
        // consumes every emitted URL across multiple ffmpegs.)
        if (playlistRandom)
            psi.ArgumentList.Add("--playlist-random");
        // Authenticate as the user's logged-in browser session — the
        // standard fix for YouTube's "Sign in to confirm you're not a bot"
        // screen. Empty/null leaves yt-dlp anonymous. Firefox is the
        // recommended browser; Chromium-based browsers (Chrome, Edge,
        // Brave, etc.) ship their cookies under app-bound encryption that
        // yt-dlp can't decrypt without further setup.
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookiesFromBrowser);
        }
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add(url);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        // Read just the first emitted line, then kill yt-dlp. Without
        // this, ReadToEndAsync would wait for stdout EOF — for a YouTube
        // playlist URL that means yt-dlp keeps resolving every item before
        // exiting (~1-2s/item with Deno-backed signature solving), blowing
        // through our 20s timeout for any non-trivial playlist. Reading
        // line-by-line gets us the first item's URL as soon as it's
        // resolved; we don't need the rest. (PlaylistAudioReader is the
        // wrapper that DOES iterate playlists across multiple ffmpegs;
        // SubprocessAudioReader is the single-shot path.)
        string? firstLine;
        try
        {
            firstLine = await proc.StandardOutput.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }

        if (string.IsNullOrEmpty(firstLine))
        {
            // yt-dlp emitted nothing — surface stderr for the actual error
            // (offline channel, private video, region lock, missing format).
            string stderr;
            try { stderr = (await proc.StandardError.ReadToEndAsync()).Trim(); }
            catch { stderr = ""; }
            var msg = string.IsNullOrEmpty(stderr) ? "yt-dlp returned no URL" : stderr;
            throw new InvalidOperationException($"yt-dlp: {msg}");
        }

        return YtDlpDisplayTitle.ParseAndCache(firstLine, url)
            ?? throw new InvalidOperationException("yt-dlp: empty URL");
    }
}
