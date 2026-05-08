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

    /// <summary>
    /// Root directory passed to yt-dlp via --plugin-dirs. Contains a single
    /// bundled plugin (yt-dlp-ChromeCookieUnlock, MIT) that releases Chrome's
    /// cookies-DB file lock so --cookies-from-browser chrome works while
    /// Chrome is running. Always populated; readers conditionally append the
    /// arg when the user has cookies-from-browser configured.
    /// </summary>
    public string YtDlpPluginsRoot { get; }

    public bool FfmpegInstalled => File.Exists(FfmpegPath);
    public bool YtDlpInstalled => File.Exists(YtDlpPath);
    public bool Ready => FfmpegInstalled && YtDlpInstalled;

    public BinaryManager(string pluginConfigDir)
    {
        BinDir = Path.Combine(pluginConfigDir, "bin");
        Directory.CreateDirectory(BinDir);
        YtDlpPluginsRoot = Path.Combine(pluginConfigDir, "yt-dlp-plugins");
        TryEnsureBundledPlugins();
    }

    /// <summary>
    /// Writes bundled yt-dlp plugins to <see cref="YtDlpPluginsRoot"/> in the
    /// directory layout yt-dlp expects (root/yt_dlp_plugins/postprocessor/...).
    /// Idempotent: rewrites only when the on-disk content differs, so plugin
    /// updates land cleanly across our own version bumps. Wrapped in try/catch
    /// because a plugin-write failure should NOT prevent ClubFFXIV from
    /// starting — yt-dlp still works without the cookie unlock, the user
    /// just won't get the Chrome cookie workaround.
    /// </summary>
    private void TryEnsureBundledPlugins()
    {
        try
        {
            var ppDir = Path.Combine(YtDlpPluginsRoot, "yt_dlp_plugins", "postprocessor");
            Directory.CreateDirectory(ppDir);
            var dest = Path.Combine(ppDir, "chrome_cookie_unlock.py");
            if (!File.Exists(dest) || File.ReadAllText(dest) != ChromeCookieUnlockSource)
            {
                File.WriteAllText(dest, ChromeCookieUnlockSource);
            }
        }
        catch
        {
            // Best-effort: if we can't write (perms, AV interference, etc.),
            // yt-dlp still works without the unlock plugin. User-visible effect
            // is that --cookies-from-browser chrome may fail with the file-lock
            // error; the UI tooltip points them at Firefox as the fallback.
        }
    }

    // ---------------------------------------------------------------------
    // Bundled MIT-licensed plugin: yt-dlp-ChromeCookieUnlock
    // https://github.com/seproDev/yt-dlp-ChromeCookieUnlock
    // (c) 2023 Charles Machalow, (c) 2024 sepro
    // Licensed under the MIT License (see plugin repo).
    // Releases Windows file lock on Chrome's cookies DB via Restart Manager
    // so yt-dlp can copy and read it while Chrome is open.
    // ---------------------------------------------------------------------
    private const string ChromeCookieUnlockSource =
@"import sys

import yt_dlp.cookies

original_func = yt_dlp.cookies._open_database_copy

def unlock_chrome(database_path, tmpdir):
    try:
        return original_func(database_path, tmpdir)
    except PermissionError:
        print('Attempting to unlock cookies', file=sys.stderr)
        unlock_cookies(database_path)
        return original_func(database_path, tmpdir)

yt_dlp.cookies._open_database_copy = unlock_chrome


# Adapted from https://gist.github.com/csm10495/e89e660ffee0030e8ef410b793ad6a7e
# By Charles Machalow under the MIT License

from ctypes import windll, byref, create_unicode_buffer, pointer, WINFUNCTYPE
from ctypes.wintypes import DWORD, WCHAR, UINT

ERROR_SUCCESS = 0
ERROR_MORE_DATA  = 234
RmForceShutdown = 1

@WINFUNCTYPE(None, UINT)
def callback(percent_complete: UINT) -> None:
    pass

rstrtmgr = windll.LoadLibrary(""Rstrtmgr"")

def unlock_cookies(cookies_path):
    session_handle = DWORD(0)
    session_flags = DWORD(0)
    session_key = (WCHAR * 256)()

    result = DWORD(rstrtmgr.RmStartSession(byref(session_handle), session_flags, session_key)).value

    if result != ERROR_SUCCESS:
        raise RuntimeError(f""RmStartSession returned non-zero result: {result}"")

    try:
        result = DWORD(rstrtmgr.RmRegisterResources(session_handle, 1, byref(pointer(create_unicode_buffer(cookies_path))), 0, None, 0, None)).value

        if result != ERROR_SUCCESS:
            raise RuntimeError(f""RmRegisterResources returned non-zero result: {result}"")

        proc_info_needed = DWORD(0)
        proc_info = DWORD(0)
        reboot_reasons = DWORD(0)

        result = DWORD(rstrtmgr.RmGetList(session_handle, byref(proc_info_needed), byref(proc_info), None, byref(reboot_reasons))).value

        if result not in (ERROR_SUCCESS, ERROR_MORE_DATA):
            raise RuntimeError(f""RmGetList returned non-successful result: {result}"")

        if proc_info_needed.value:
            result = DWORD(rstrtmgr.RmShutdown(session_handle, RmForceShutdown, callback)).value

            if result != ERROR_SUCCESS:
                raise RuntimeError(f""RmShutdown returned non-successful result: {result}"")
        else:
            print(""File is not locked"")
    finally:
        result = DWORD(rstrtmgr.RmEndSession(session_handle)).value

        if result != ERROR_SUCCESS:
            raise RuntimeError(f""RmEndSession returned non-successful result: {result}"")
";

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

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token).WaitAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token).WaitAsync(CancellationToken.None);
        await proc.WaitForExitAsync(cts.Token);
        return (await stdoutTask, await stderrTask);
    }
}
