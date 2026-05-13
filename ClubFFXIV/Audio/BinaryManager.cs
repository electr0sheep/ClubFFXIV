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
/// Downloads, locates, and updates the external ffmpeg, yt-dlp, and Deno
/// binaries used by SubprocessAudioReader. All live in the plugin config
/// directory under "bin/" so they survive plugin updates and don't pollute
/// system PATH.
///
/// yt-dlp self-updates via "yt-dlp -U" — we just invoke that.
/// ffmpeg has no self-update; we re-download from BtbN's static GPL builds when
/// requested (rarely needed; ffmpeg core is stable).
/// Deno is yt-dlp's recommended JavaScript runtime, used to solve YouTube's
/// signature/n-challenge JS. Without it, yt-dlp can't extract most YouTube
/// formats ("Requested format is not available" / "Only images are available").
/// </summary>
public sealed class BinaryManager
{
    // BtbN's static GPL build is a single-file ffmpeg.exe inside a zip — no DLLs.
    private const string FfmpegZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string YtDlpUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    // Single-file deno.exe inside the zip.
    private const string DenoZipUrl =
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    public string BinDir { get; }
    public string FfmpegPath => Path.Combine(BinDir, "ffmpeg.exe");
    public string YtDlpPath => Path.Combine(BinDir, "yt-dlp.exe");
    public string DenoPath => Path.Combine(BinDir, "deno.exe");

    public bool FfmpegInstalled => File.Exists(FfmpegPath);
    public bool YtDlpInstalled => File.Exists(YtDlpPath);
    public bool DenoInstalled => File.Exists(DenoPath);
    public bool Ready => FfmpegInstalled && YtDlpInstalled && DenoInstalled;

    public BinaryManager(string pluginConfigDir)
    {
        BinDir = Path.Combine(pluginConfigDir, "bin");
        Directory.CreateDirectory(BinDir);
    }

    /// <summary>
    /// Builds the canonical yt-dlp <see cref="ProcessStartInfo"/> for both the
    /// single-shot URL resolver (<see cref="SubprocessAudioReader.ResolveUrlAsync"/>)
    /// and the long-lived lazy playlist (<see cref="PlaylistAudioReader.CreateAsync"/>).
    /// Centralised so the --print template, the Deno JS-runtime hookup, and
    /// the cookies/playlist-random toggles only have to change in one place.
    /// </summary>
    public ProcessStartInfo BuildYtDlpStartInfo(
        string url, bool playlistRandom, string? cookiesFromBrowser)
    {
        var psi = new ProcessStartInfo
        {
            FileName = YtDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Combined URL+metadata template (replaces -g). yt-dlp's --print
        // implies --simulate, so the format is selected and resolved but
        // not downloaded. Metadata fields drive the Now Playing label.
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add(YtDlpDisplayTitle.PrintTemplate);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("bestaudio/best");
        // For URLs that are both a video and a playlist (/watch?v=X&list=Y),
        // --no-playlist tells yt-dlp to download the video, not the playlist.
        psi.ArgumentList.Add("--no-playlist");
        // Point yt-dlp at our bundled Deno binary so it can solve YouTube's
        // signature/n-challenge JS. Without a JS runtime, yt-dlp falls back
        // to "Only images are available" for most YouTube videos.
        psi.ArgumentList.Add("--js-runtimes");
        psi.ArgumentList.Add($"deno:{DenoPath}");
        // For pure playlist URLs, --playlist-random shuffles before emit;
        // off, yt-dlp emits items in original order.
        if (playlistRandom)
            psi.ArgumentList.Add("--playlist-random");
        // Authenticate as the user's logged-in browser session — the
        // standard fix for YouTube's "Sign in to confirm you're not a bot"
        // screen. Empty/null leaves yt-dlp anonymous. Firefox is the
        // recommended browser; Chromium-based browsers ship cookies under
        // app-bound encryption that yt-dlp can't decrypt without setup.
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookiesFromBrowser);
        }
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add(url);
        return psi;
    }

    /// <summary>
    /// Builds the inner yt-dlp invocation that downloads the compressed audio
    /// to stdout. Used by <see cref="SubprocessAudioReader"/> as the network
    /// layer in front of ffmpeg — yt-dlp does chunked HTTP Range fetches
    /// (<c>--http-chunk-size</c>) which is the community-documented workaround
    /// for googlevideo throttling. The HN thread on bypassing YouTube throttling
    /// calls out that piping a bare URL to ffmpeg/mpv defeats this; running the
    /// bytes through yt-dlp's HttpFD restores it.
    ///
    /// The inner downloader receives an already-resolved googlevideo URL from
    /// the outer <see cref="BuildYtDlpStartInfo"/>'s <c>--print</c> resolve, so
    /// no extractor work happens here — yt-dlp's GenericIE passes the URL
    /// through and HttpFD handles the byte stream.
    /// </summary>
    public ProcessStartInfo BuildYtDlpDownloadStartInfo(string mediaUrl)
    {
        var psi = new ProcessStartInfo
        {
            FileName = YtDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("-");
        // The actual fix: 10 MiB Range-bounded chunks. yt-dlp FAQ notes
        // googlevideo throttles requests asking for > 10 MiB at once, so this
        // size is the documented sweet spot.
        psi.ArgumentList.Add("--http-chunk-size"); psi.ArgumentList.Add("10M");
        // Belt-and-suspenders: if effective rate ever drops below 100 KB/s yt-dlp
        // tears the connection down and re-establishes. Limited utility here
        // since we're handing a pre-resolved URL (re-extraction has nothing to
        // do), but the connection rebuild itself often dodges the throttle.
        psi.ArgumentList.Add("--throttled-rate"); psi.ArgumentList.Add("100K");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-progress");
        psi.ArgumentList.Add("--no-part");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add(mediaUrl);
        return psi;
    }

    /// <summary>
    /// Ensures all three binaries exist on disk, downloading whichever is missing.
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
        if (!DenoInstalled)
        {
            progress?.Invoke("Downloading Deno (~40 MB)...");
            await DownloadDenoAsync(ct);
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

    /// <summary>
    /// Forces a re-download of Deno. Deno releases roughly every 6 weeks; rarely
    /// needs updating mid-life-cycle, but here for parity with the other binaries.
    /// </summary>
    public async Task<string> UpdateDenoAsync(CancellationToken ct = default)
    {
        await DownloadDenoAsync(ct);
        return await GetDenoVersionAsync(ct);
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

    public async Task<string> GetDenoVersionAsync(CancellationToken ct = default)
    {
        if (!DenoInstalled) return "(not installed)";
        try
        {
            // "deno --version" prints multiple lines; first is "deno X.Y.Z (...)"
            var (output, _) = await RunCaptureAsync(DenoPath, "--version", TimeSpan.FromSeconds(15), ct);
            var firstLine = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
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
        File.Move(tmpPath, YtDlpPath, overwrite: true);
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

        File.Move(tmpPath, FfmpegPath, overwrite: true);
    }

    private async Task DownloadDenoAsync(CancellationToken ct)
    {
        var tmpPath = DenoPath + ".new";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClubFFXIV/0.1");

        // Single deno.exe inside a flat zip — no nested directories to navigate.
        using var zipStream = await http.GetStreamAsync(DenoZipUrl, ct);
        using var memBuffer = new MemoryStream();
        await zipStream.CopyToAsync(memBuffer, ct);
        memBuffer.Position = 0;

        using (var zip = new ZipArchive(memBuffer, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidOperationException("deno.exe not found in Deno release zip — upstream layout changed.");

            using var entryStream = entry.Open();
            using var fs = File.Create(tmpPath);
            await entryStream.CopyToAsync(fs, ct);
        }

        File.Move(tmpPath, DenoPath, overwrite: true);
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

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token).WaitAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token).WaitAsync(CancellationToken.None);
        await proc.WaitForExitAsync(cts.Token);
        return (await stdoutTask, await stderrTask);
    }
}
