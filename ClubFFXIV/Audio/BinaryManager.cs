using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClubFFXIV.Audio;

/// <summary>
/// Downloads, locates, and updates the external ffmpeg + yt-dlp binaries used
/// by SubprocessAudioReader. Both live in the plugin config directory under
/// "bin/" so they survive plugin updates and don't pollute system PATH.
///
/// yt-dlp self-updates via "yt-dlp -U" — we just invoke that.
/// ffmpeg has no self-update; we re-download from BtbN's static GPL builds when
/// requested (rarely needed; ffmpeg core is stable).
/// </summary>
public sealed class BinaryManager
{
    // BtbN's static GPL build is a single-file ffmpeg.exe inside a zip — no DLLs.
    private const string FfmpegZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string YtDlpUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public string BinDir { get; }
    public string FfmpegPath => Path.Combine(BinDir, "ffmpeg.exe");
    public string YtDlpPath => Path.Combine(BinDir, "yt-dlp.exe");

    public bool FfmpegInstalled => File.Exists(FfmpegPath);
    public bool YtDlpInstalled => File.Exists(YtDlpPath);
    public bool Ready => FfmpegInstalled && YtDlpInstalled;

    public BinaryManager(string pluginConfigDir)
    {
        BinDir = Path.Combine(pluginConfigDir, "bin");
        Directory.CreateDirectory(BinDir);
    }

    /// <summary>
    /// Ensures both binaries exist on disk, downloading whichever is missing.
    /// Reports progress via the optional callback ("Downloading yt-dlp...").
    /// </summary>
    public async Task EnsureInstalledAsync(
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!YtDlpInstalled)
        {
            progress?.Invoke("Downloading yt-dlp (~3 MB)...");
            await DownloadYtDlpAsync(ct);
        }
        if (!FfmpegInstalled)
        {
            progress?.Invoke("Downloading ffmpeg (~80 MB)...");
            await DownloadFfmpegAsync(ct);
        }
    }

    /// <summary>
    /// Forces a re-download of yt-dlp (newest from GitHub releases).
    /// Returns the version reported by `yt-dlp --version` after install.
    /// </summary>
    public async Task<string> UpdateYtDlpAsync(CancellationToken ct = default)
    {
        // Prefer yt-dlp's own update mechanism when it's already installed —
        // it's faster and atomic on the binary's terms.
        if (YtDlpInstalled)
        {
            try
            {
                await RunCaptureAsync(YtDlpPath, "-U", TimeSpan.FromMinutes(2), ct);
            }
            catch
            {
                // Fall back to fresh download if -U failed (permission issue,
                // network glitch, etc.).
                await DownloadYtDlpAsync(ct);
            }
        }
        else
        {
            await DownloadYtDlpAsync(ct);
        }
        return await GetYtDlpVersionAsync(ct);
    }

    /// <summary>
    /// Forces a re-download of ffmpeg. ffmpeg core is stable; rarely needed.
    /// </summary>
    public async Task<string> UpdateFfmpegAsync(CancellationToken ct = default)
    {
        await DownloadFfmpegAsync(ct);
        return await GetFfmpegVersionAsync(ct);
    }

    public async Task<string> GetYtDlpVersionAsync(CancellationToken ct = default)
    {
        if (!YtDlpInstalled) return "(not installed)";
        try
        {
            var (output, _) = await RunCaptureAsync(YtDlpPath, "--version", TimeSpan.FromSeconds(15), ct);
            return output.Trim();
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
    }

    public async Task<string> GetFfmpegVersionAsync(CancellationToken ct = default)
    {
        if (!FfmpegInstalled) return "(not installed)";
        try
        {
            // ffmpeg writes version to stderr, banner is "ffmpeg version X.Y.Z ..."
            var (_, stderr) = await RunCaptureAsync(FfmpegPath, "-version", TimeSpan.FromSeconds(15), ct);
            var firstLine = stderr.Split('\n').FirstOrDefault()?.Trim() ?? "";
            // "ffmpeg version N-XXXXX-gXXXXXXXX-..." — keep the first ~40 chars
            return firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
    }

    private async Task DownloadYtDlpAsync(CancellationToken ct)
    {
        var tmpPath = YtDlpPath + ".new";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClubFFXIV/0.1");
            var bytes = await http.GetByteArrayAsync(YtDlpUrl, ct);
            await File.WriteAllBytesAsync(tmpPath, bytes, ct);
        }
        // Atomic replace so an in-flight subprocess keeps using the old copy.
        if (File.Exists(YtDlpPath)) File.Delete(YtDlpPath);
        File.Move(tmpPath, YtDlpPath);
    }

    private async Task DownloadFfmpegAsync(CancellationToken ct)
    {
        var tmpPath = FfmpegPath + ".new";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClubFFXIV/0.1");

        // Stream the zip to memory (it's ~80MB; acceptable peak), find ffmpeg.exe,
        // extract just that.
        using var zipStream = await http.GetStreamAsync(FfmpegZipUrl, ct);
        using var memBuffer = new MemoryStream();
        await zipStream.CopyToAsync(memBuffer, ct);
        memBuffer.Position = 0;

        using (var zip = new ZipArchive(memBuffer, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidOperationException("ffmpeg.exe not found in BtbN zip — upstream layout changed.");

            using var entryStream = entry.Open();
            using var fs = File.Create(tmpPath);
            await entryStream.CopyToAsync(fs, ct);
        }

        if (File.Exists(FfmpegPath)) File.Delete(FfmpegPath);
        File.Move(tmpPath, FfmpegPath);
    }

    private static async Task<(string stdout, string stderr)> RunCaptureAsync(
        string executable, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {executable}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token).AsTask();
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token).AsTask();
        await proc.WaitForExitAsync(cts.Token);
        return (await stdoutTask, await stderrTask);
    }
}
