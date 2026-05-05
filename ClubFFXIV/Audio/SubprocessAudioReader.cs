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
public sealed class SubprocessAudioReader : ISampleProvider, IDisposable
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

    private SubprocessAudioReader(Process ffmpeg)
    {
        this.ffmpeg = ffmpeg;
        this.stdout = ffmpeg.StandardOutput.BaseStream;
    }

    /// <summary>
    /// Resolves the URL via yt-dlp, then spawns ffmpeg to decode the resolved
    /// stream into raw PCM. Throws on yt-dlp failure (offline channel, bad URL).
    /// </summary>
    public static async Task<SubprocessAudioReader> CreateAsync(
        string url, BinaryManager binaries, CancellationToken ct = default)
    {
        if (!binaries.Ready)
            throw new InvalidOperationException("ffmpeg/yt-dlp not yet installed");

        // Step 1 — let yt-dlp pick the best audio-only or low-bitrate variant
        // and print the underlying media URL.
        var resolvedUrl = await ResolveUrlAsync(url, binaries, ct);

        // Step 2 — spawn ffmpeg, pipe PCM to stdout.
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

        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(resolvedUrl);
        psi.ArgumentList.Add("-vn");                 // drop video
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("pipe:1");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg");

        var reader = new SubprocessAudioReader(proc);

        // Drain stderr in the background so it doesn't fill its pipe and block ffmpeg.
        // Per-line at Info so we see ffmpeg's chatter without changing log filters.
        // Exit warning only fires when WE didn't kill the process — distinguishes
        // "ffmpeg crashed on its own" (real bug) from "we killed it via Dispose"
        // (normal stream switch / shutdown).
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
                if (n == 0) break;
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

    private static async Task<string> ResolveUrlAsync(
        string url, BinaryManager binaries, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaries.YtDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-g");                   // print direct media URL only
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("bestaudio/best");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add(url);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token).WaitAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token).WaitAsync(CancellationToken.None);
        await proc.WaitForExitAsync(cts.Token);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (proc.ExitCode != 0)
        {
            // Common cases: channel offline, video private, region-locked.
            var msg = string.IsNullOrEmpty(stderr) ? "yt-dlp failed (no error output)" : stderr;
            throw new InvalidOperationException($"yt-dlp: {msg}");
        }

        // -g may print multiple URLs (one per format); take the first.
        var firstUrl = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } lines
            ? lines[0].Trim()
            : "";
        if (string.IsNullOrEmpty(firstUrl))
            throw new InvalidOperationException("yt-dlp returned no URL");

        return firstUrl;
    }
}
