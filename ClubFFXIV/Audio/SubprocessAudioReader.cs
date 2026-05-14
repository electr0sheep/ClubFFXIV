using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Audio source backed by an ffmpeg subprocess that decodes a yt-dlp-resolved
/// stream into raw 32-bit float PCM at 44.1 kHz stereo. Exposes the output
/// as an NAudio ISampleProvider.
///
/// Pipeline (v2 — chunked-download path):
///   yt-dlp -g &lt;watch&gt;       → outer process emits the resolved CDN URL
///   yt-dlp -o - --http-chunk-size 10M &lt;cdn-url&gt;   → byte stream on stdout
///       │
///       ├─→ on-disk cache (tempFile, grows as bytes arrive)
///       └─→ ffmpeg -i pipe:0 -f f32le pipe:1                    → PCM
///
/// The chunked yt-dlp downloader is the documented workaround for googlevideo
/// throttling long single-connection HTTP reads. Piping a bare URL to ffmpeg
/// (as the v1 path did) bypasses that machinery and stalls indefinitely once
/// the throttle kicks in (~14 min into long videos). See the project notes
/// for the community-doc trail.
///
/// The tee-to-tempFile gives us instant local seeks for any timestamp that's
/// already been streamed; targets past the cache head fall back to a fresh
/// ffmpeg-against-URL with <c>-ss</c> (preserving today's snappy-seek UX).
///
/// Stops all subprocesses on Dispose. Network/decoder errors return 0 from
/// Read, which signals end-of-stream to NAudio's WaveOutEvent — playback
/// stops cleanly.
/// </summary>
public sealed class SubprocessAudioReader : ISampleProvider, IDisposable, ICleanExitSource, ISeekableSource
{
    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    // === Mode state machine =================================================
    //
    // Three playback modes, distinguished by where ffmpeg's input comes from:
    //
    //   Live      ffmpeg.stdin ← tee pump ← yt-dlp.stdout   (initial play)
    //   FileSeek  ffmpeg -ss N -i &lt;tempFile&gt;               (seek-to-cached)
    //   UrlSeek   ffmpeg -ss N -i &lt;cdn-url&gt; (with reconnect) (seek-to-uncached)
    //
    // The download controller (yt-dlp + tee pump) runs independently of the
    // playback controller — it stays alive across mode switches so the cache
    // keeps filling even after a seek that tore down the live ffmpeg. When
    // ytdlp finishes naturally, downloadComplete flips true and any future
    // seek is guaranteed to find its target in the cache.
    private enum PlaybackMode { Live, FileSeek, UrlSeek }

    // === Download controller ================================================
    private Process? ytdlpProc;
    private FileStream? tempFileStream;
    private Task? teePumpTask;
    private readonly CancellationTokenSource downloadCts = new();
    private readonly string tempFilePath;
    // Bytes confirmed written to the on-disk cache. Read by seek logic to
    // decide FileSeek vs. UrlSeek. Written only by the tee pump.
    private long tempFileLength;
    // Set true when the tee pump observes EOF on yt-dlp's stdout AND yt-dlp
    // itself exited with status 0. Means: tempFile is sealed and contains
    // the entire compressed audio — every seek can use FileSeek from now on.
    private volatile bool downloadComplete;
    // Set true on a network / process error that prevents the cache from
    // completing. Seeks past the current cache head will still try UrlSeek,
    // but the watchdog won't expect FileSeek to ever become available for
    // unfilled regions.
    private volatile bool downloadFailed;
    // Live mode's tee target — ffmpeg-live's stdin. Cleared (set null) when
    // we tear down the live ffmpeg (seek, watchdog respawn). Tee pump
    // continues writing to tempFile regardless.
    private Stream? liveFfmpegStdin;
    private readonly object teeLock = new();

    // === Playback controller ================================================
    private Process ffmpegProc = null!;
    private Stream ffmpegStdout = null!;
    private PlaybackMode mode;
    // True only when our Dispose actively killed a still-running process.
    // Stays false when ffmpeg already exited on its own — so the stderr
    // drain can correctly attribute unexpected exits.
    private bool killedByUs;
    private bool eofLogged;
    // Single-fire guard for NaturalExited.
    private bool naturalExitFired;
    private bool disposed;
    private byte[] readBuffer = new byte[8192];

    // 1Hz progress watchdog. Logs `[ffmpeg] STALL` when samplesPlayed hasn't
    // advanced for ≥3s while a Read is in flight. In v2 the watchdog also
    // triggers a recovery: after a stall threshold it kills the current
    // playback ffmpeg and respawns at the current position — in FileSeek
    // mode if the cache covers it (instant), otherwise UrlSeek.
    private int readsInFlight;
    private readonly Task watchdogTask;
    private readonly CancellationTokenSource watchdogCts = new();

    /// <summary>
    /// Fires once when ffmpeg exits without our Dispose/seek having killed it
    /// — natural EOF or unexpected crash. Lets <see cref="PlaylistAudioReader"/>
    /// pre-trigger the next item's spawn in parallel with the OS pipe + WaveOut
    /// buffer draining the dying ffmpeg's remaining audio.
    /// </summary>
    public event Action? NaturalExited;

    // Stored so seek + watchdog respawns can rebuild ffmpeg without going
    // through yt-dlp's --print again.
    private readonly string resolvedMediaUrl;
    private readonly BinaryManager binaries;
    // Bytes per second estimate for converting "seek to seconds T" into "is
    // byte offset (T × bytesPerSec) inside tempFile?". Pulled from yt-dlp's
    // %(abr)s metadata via BitrateKbpsCache; falls back to 24000 bytes/sec
    // (192 kbps, conservative overestimate for typical 128 kbps content).
    // Overestimating is the safe direction — we under-report cache coverage
    // and route ambiguous seeks through UrlSeek, which always works.
    private readonly double bytesPerSec;

    // Coordinates the audio-thread Read against UI-thread SeekToSeconds
    // (same protocol as v1: pendingSeekSeconds < 0 means no seek pending).
    private readonly object swapLock = new();
    private double pendingSeekSeconds = -1;
    // Total float samples the consumer has read since playback started (or
    // since the last seek). After a seek, reset to seekTarget * SampleRate
    // * Channels so the UI position display tracks the new playhead.
    private long samplesPlayed;

    private SubprocessAudioReader(string resolvedMediaUrl, BinaryManager binaries)
    {
        this.resolvedMediaUrl = resolvedMediaUrl;
        this.binaries = binaries;
        this.tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"clubffxiv_{Guid.NewGuid():N}.aud");

        var cachedKbps = BitrateKbpsCache.Get(resolvedMediaUrl);
        // 192 kbps default = 24,000 B/s. Most YouTube AAC is 128 kbps so this
        // overestimates and biases the seek-mode decision toward UrlSeek.
        this.bytesPerSec = cachedKbps > 0 ? cachedKbps * 1000.0 / 8.0 : 24_000.0;

        this.watchdogTask = Task.Run(() => WatchdogLoop(watchdogCts.Token));
    }

    /// <summary>
    /// Resolves the URL via yt-dlp, then spawns the download + decode pipeline.
    /// Throws on yt-dlp failure (offline channel, bad URL). For playlist URLs,
    /// <paramref name="playlistRandom"/> picks shuffle vs. lazy-iterate mode.
    /// Both yield the first emitted item; single-video URLs ignore both flags.
    /// </summary>
    public static async Task<SubprocessAudioReader> CreateAsync(
        string url, BinaryManager binaries, bool playlistRandom = false,
        string? cookiesFromBrowser = null,
        CancellationToken ct = default)
    {
        if (!binaries.Ready)
            throw new InvalidOperationException("ffmpeg/yt-dlp not yet installed");

        var resolvedUrl = await ResolveUrlAsync(url, binaries, playlistRandom, cookiesFromBrowser, ct);
        return FromResolvedUrl(resolvedUrl, binaries, ct);
    }

    /// <summary>
    /// Spawn the download + decode pipeline against an already-resolved media
    /// URL (no outer yt-dlp involvement). Used by <see cref="PlaylistAudioReader"/>
    /// after pulling the next item URL from a long-lived yt-dlp's stdout.
    /// </summary>
    public static SubprocessAudioReader FromResolvedUrl(
        string resolvedMediaUrl, BinaryManager binaries, CancellationToken ct = default)
    {
        if (!binaries.Ready)
            throw new InvalidOperationException("ffmpeg/yt-dlp not yet installed");

        var reader = new SubprocessAudioReader(resolvedMediaUrl, binaries);
        try
        {
            reader.StartLivePipeline(ct);
        }
        catch
        {
            // Pipeline spawn failed (binary not found, OOM, etc). Tear down
            // the watchdog task and any half-initialised state before
            // propagating, otherwise the watchdog task leaks for the
            // lifetime of the process.
            try { reader.Dispose(); } catch { }
            throw;
        }
        return reader;
    }

    // === Live pipeline startup ==============================================

    private void StartLivePipeline(CancellationToken ct)
    {
        // Step 1: open the on-disk cache file. Mode.Create truncates any
        // stale file at this path (extremely unlikely with GUID names but
        // defensive). FileShare.Read lets a FileSeek-mode ffmpeg open it
        // concurrently for reading while we're still appending.
        tempFileStream = new FileStream(
            tempFilePath, FileMode.Create, FileAccess.Write,
            FileShare.Read, 64 * 1024);

        // Step 2: spawn the inner yt-dlp downloader. It receives the already-
        // resolved CDN URL, so no extractor work runs here — yt-dlp's HttpFD
        // just does chunked Range fetches and writes them to stdout.
        var ytdlpPsi = binaries.BuildYtDlpDownloadStartInfo(resolvedMediaUrl);
        ytdlpProc = Process.Start(ytdlpPsi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp downloader");
        ytdlpProc.EnableRaisingEvents = true;

        // Step 3: spawn ffmpeg in stdin-piped mode and wire it up as the
        // initial playback. Its stdin is the tee's secondary target.
        var ffmpeg = SpawnFfmpegStdin();
        InstallPlaybackProc(ffmpeg, PlaybackMode.Live, ffmpegStdinForTee: ffmpeg.StandardInput.BaseStream);

        // Step 4: start the tee pump. It owns the yt-dlp stdout stream from
        // now on — reads in chunks, fans out to tempFile and the live ffmpeg.
        var ytdlpStdout = ytdlpProc.StandardOutput.BaseStream;
        teePumpTask = Task.Run(() => TeePumpLoop(ytdlpStdout, downloadCts.Token));

        Plugin.Log.Info(
            $"[pipeline] live spawned. ytdlp_pid={ytdlpProc.Id} " +
            $"ffmpeg_pid={ffmpeg.Id} bytesPerSec≈{bytesPerSec:F0} " +
            $"tempFile={Path.GetFileName(tempFilePath)} " +
            $"url={TruncateUrl(resolvedMediaUrl)}");

        ProcessLog.DrainStderrInBackground(ytdlpProc, "yt-dlp-dl", ct);
    }

    /// <summary>
    /// Build a stdin-pipe ffmpeg. Reads raw compressed audio from pipe:0,
    /// decodes to f32le PCM on pipe:1. No <c>-ss</c>, no reconnect flags —
    /// reconnection happens upstream in yt-dlp.
    /// </summary>
    private Process SpawnFfmpegStdin()
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaries.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("warning");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("pipe:1");

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg (stdin)");
    }

    /// <summary>
    /// Build a file-input ffmpeg with fast seek to <paramref name="startSec"/>.
    /// Pre-input <c>-ss</c> jumps to the keyframe at-or-before the requested
    /// time without decoding the whole prefix — same fast-seek trick as v1's
    /// URL ffmpeg, but against the local cache file (seekable, no network).
    /// </summary>
    private Process SpawnFfmpegFile(double startSec)
    {
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
        if (startSec > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startSec.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(tempFilePath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("pipe:1");

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg (file)");
    }

    /// <summary>
    /// Build a direct-URL ffmpeg with fast seek to <paramref name="startSec"/>.
    /// Used when the seek target hasn't been downloaded into the cache yet.
    /// Keeps the v1 reconnect flags so the connection can recover from
    /// transient errors, plus a -rw_timeout so a wedged socket eventually
    /// surfaces as an error to the watchdog instead of hanging forever.
    /// </summary>
    private Process SpawnFfmpegUrl(double startSec)
    {
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
        psi.ArgumentList.Add("-rw_timeout"); psi.ArgumentList.Add("15000000");
        psi.ArgumentList.Add("-reconnect"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max"); psi.ArgumentList.Add("5");
        if (startSec > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startSec.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(resolvedMediaUrl);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("pipe:1");

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg (url)");
    }

    /// <summary>
    /// Mounts a freshly-spawned ffmpeg as the current playback source. Hooks
    /// stderr drain + Exited handler, swaps in the new ffmpeg/stdout fields,
    /// and updates the tee's live target so it knows whether to fan bytes
    /// into the playback ffmpeg's stdin (Live mode) or just to disk (post-seek
    /// modes).
    /// </summary>
    private void InstallPlaybackProc(Process proc, PlaybackMode newMode, Stream? ffmpegStdinForTee)
    {
        proc.EnableRaisingEvents = true;
        ffmpegProc = proc;
        ffmpegStdout = proc.StandardOutput.BaseStream;
        mode = newMode;
        eofLogged = false;

        lock (teeLock) { liveFfmpegStdin = ffmpegStdinForTee; }

        proc.Exited += OnFfmpegExited;
        if (proc.HasExited) OnFfmpegExited(proc, EventArgs.Empty);
        ProcessLog.DrainStderrInBackground(proc, "ffmpeg", CancellationToken.None,
            onDrainComplete: () =>
            {
                if (proc.HasExited && !killedByUs)
                    Plugin.Log.Warning($"[ffmpeg] exited unexpectedly with code {proc.ExitCode}");
            });
    }

    // === Tee pump ===========================================================

    /// <summary>
    /// Read yt-dlp's stdout in chunks and fan each chunk to two destinations:
    ///  • tempFile (always, so the on-disk cache always reflects every byte
    ///    yt-dlp delivered — this is what makes post-seek FileSeek work).
    ///  • liveFfmpegStdin (only when a Live-mode ffmpeg is mounted; cleared
    ///    on mode switches so seek-mode ffmpegs don't get confused by stray
    ///    writes to a stdin they don't own).
    /// Exits on EOF (download done), cancellation (Dispose), or a write error.
    /// </summary>
    private async Task TeePumpLoop(Stream from, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try { n = await from.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (n == 0) break; // yt-dlp EOF — download complete

                if (tempFileStream != null)
                {
                    try
                    {
                        await tempFileStream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        Interlocked.Add(ref tempFileLength, n);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"[tee] tempFile write failed: {ex.Message}");
                        downloadFailed = true;
                        return;
                    }
                }

                Stream? sec;
                lock (teeLock) { sec = liveFfmpegStdin; }
                if (sec != null)
                {
                    try
                    {
                        await sec.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // ffmpeg-live closed its stdin (exited, or we swapped
                        // it out for a seek). Drop the secondary target; tee
                        // keeps writing to tempFile so the cache fills in.
                        lock (teeLock) { if (liveFfmpegStdin == sec) liveFfmpegStdin = null; }
                    }
                    catch (OperationCanceledException) { return; }
                }
            }

            if (!ct.IsCancellationRequested)
            {
                // Wait for yt-dlp itself to exit so we can check its status.
                // Without this the cache might be marked "complete" while
                // yt-dlp is still flushing a non-zero exit code.
                try
                {
                    if (ytdlpProc is { } yp)
                        await yp.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                var exitOk = ytdlpProc?.ExitCode == 0;
                if (exitOk)
                {
                    downloadComplete = true;
                    Plugin.Log.Info(
                        $"[tee] download complete. " +
                        $"bytes={Interlocked.Read(ref tempFileLength)} " +
                        $"tempFile={Path.GetFileName(tempFilePath)}");
                }
                else
                {
                    downloadFailed = true;
                    Plugin.Log.Warning(
                        $"[tee] yt-dlp exited with code {ytdlpProc?.ExitCode} " +
                        $"after {Interlocked.Read(ref tempFileLength)} bytes");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[tee] pump error: {ex.Message}");
            downloadFailed = true;
        }
        finally
        {
            // Flush and close the temp file so FileSeek-mode ffmpegs see a
            // consistent view. Close ffmpeg-live's stdin so it can flush
            // any pending decoder buffer and exit naturally.
            try { tempFileStream?.Flush(); } catch { }
            Stream? secFinal;
            lock (teeLock) { secFinal = liveFfmpegStdin; liveFfmpegStdin = null; }
            try { secFinal?.Close(); } catch { }
        }
    }

    // === Read / position ====================================================

    public int Read(float[] buffer, int offset, int count)
    {
        if (disposed) return 0;
        Interlocked.Increment(ref readsInFlight);
        try
        {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            // Service any pending seek before reading. Done on the audio
            // thread so the kill+respawn races no in-flight Read.
            ApplyPendingSeekIfAny();

            var byteCount = count * 4;
            if (readBuffer.Length < byteCount)
                readBuffer = new byte[byteCount];

            var currentStream = ffmpegStdout;
            int totalBytes = 0;
            try
            {
                while (totalBytes < byteCount)
                {
                    var n = currentStream.Read(readBuffer, totalBytes, byteCount - totalBytes);
                    if (n == 0) break;
                    totalBytes += n;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Subprocess audio read error: {ex.Message}");
                return 0;
            }

            if (totalBytes > 0)
            {
                var samples = MemoryMarshal.Cast<byte, float>(readBuffer.AsSpan(0, totalBytes));
                samples.CopyTo(buffer.AsSpan(offset));
                var sampleCount = totalBytes / 4;
                Interlocked.Add(ref samplesPlayed, sampleCount);
                return sampleCount;
            }

            // EOF from current ffmpeg. Three possibilities:
            //   1. Genuine end-of-track (downloadComplete + ffmpeg drained
            //      the whole cache / URL). Surrender so NAudio stops cleanly.
            //   2. Pending seek that killed ffmpeg — second attempt picks
            //      it up via ApplyPendingSeekIfAny.
            //   3. We're in FileSeek mode and ran past the cache head while
            //      yt-dlp is still downloading — kick a UrlSeek respawn at
            //      the current playhead so the user doesn't hear a silent
            //      premature end.
            bool seekPending;
            lock (swapLock) seekPending = pendingSeekSeconds >= 0;
            if (seekPending) continue;

            if (mode == PlaybackMode.FileSeek && !downloadComplete && !downloadFailed)
            {
                // Force UrlSeek directly rather than routing through
                // pendingSeekSeconds + PickSeekModeAndSpawn — the latter
                // could pick FileSeek again if our bytesPerSec estimate
                // happened to underbid the true rate, leading to a stall
                // loop until attempts exhausted. Direct force guarantees
                // forward progress.
                var pos = PositionSeconds;
                Plugin.Log.Info(
                    $"[ffmpeg] FileSeek outran download cache at " +
                    $"pos={pos:F1}s — forcing UrlSeek");
                SwitchToUrlSeek(pos);
                continue;
            }

            if (!eofLogged)
            {
                eofLogged = true;
                Plugin.Log.Warning(
                    $"[ffmpeg] stdout EOF — mode={mode} " +
                    $"HasExited={ffmpegProc.HasExited} killedByUs={killedByUs} " +
                    $"downloadComplete={downloadComplete} downloadFailed={downloadFailed}");
            }
            return 0;
        }
        return 0;
        }
        finally
        {
            Interlocked.Decrement(ref readsInFlight);
        }
    }

    // === Watchdog (diagnostic) ==============================================

    /// <summary>
    /// 1Hz watchdog. Diagnostic-only — logs <c>[ffmpeg] STALL</c> when
    /// samplesPlayed hasn't moved for ≥3s with a Read in flight, and
    /// <c>[ffmpeg] STALL RESOLVED</c> when samples flow again.
    ///
    /// Deliberately does NOT take any recovery action. The byte stream is
    /// yt-dlp's responsibility — its HttpFD does chunked Range fetches with
    /// per-chunk retry, which is the community-tested mechanism for surviving
    /// googlevideo's throttling. An earlier version of this watchdog also
    /// killed ffmpeg and respawned the pipeline; that overlapped with
    /// yt-dlp's own recovery and introduced races between the kill-and-
    /// respawn loop and other state transitions (seek, item advance). If
    /// yt-dlp ever fails to recover, the STALL log is the signal that lets
    /// us tell — but we don't try to "fix" yt-dlp from inside the reader.
    /// </summary>
    private async Task WatchdogLoop(CancellationToken ct)
    {
        long lastSamples = 0;
        var lastProgressAt = DateTime.UtcNow;
        bool firstSampleSeen = false;
        bool stalled = false;
        DateTime nextStallLog = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (disposed) return;

            var now = DateTime.UtcNow;
            var current = Interlocked.Read(ref samplesPlayed);

            if (current != lastSamples)
            {
                if (stalled)
                {
                    var stallDuration = (now - lastProgressAt).TotalSeconds;
                    Plugin.Log.Warning(
                        $"[ffmpeg] STALL RESOLVED mode={mode} " +
                        $"ageSec={stallDuration:F1} samplesPlayed={current}");
                    stalled = false;
                }
                lastSamples = current;
                lastProgressAt = now;
                firstSampleSeen = true;
                continue;
            }

            if (!firstSampleSeen) continue;
            var idle = (now - lastProgressAt).TotalSeconds;
            if (idle < 3) continue;
            if (Interlocked.CompareExchange(ref readsInFlight, 0, 0) == 0) continue;

            if (!stalled || now >= nextStallLog)
            {
                var currentFfmpeg = ffmpegProc;
                bool ffmpegAlive;
                string exitInfo = "n/a";
                try
                {
                    ffmpegAlive = !currentFfmpeg.HasExited;
                    if (!ffmpegAlive) exitInfo = currentFfmpeg.ExitCode.ToString();
                }
                catch { ffmpegAlive = false; }

                Plugin.Log.Warning(
                    $"[ffmpeg] STALL pid={SafePid()} mode={mode} ffmpegAlive={ffmpegAlive} " +
                    $"exitCode={exitInfo} samplesPlayed={current} ageSec={idle:F1} " +
                    $"cacheBytes={Interlocked.Read(ref tempFileLength)} " +
                    $"downloadComplete={downloadComplete} " +
                    $"url={TruncateUrl(resolvedMediaUrl)}");
                stalled = true;
                nextStallLog = now.AddSeconds(10);
            }
        }
    }

    private int SafePid()
    {
        try { return ffmpegProc.Id; } catch { return -1; }
    }

    private static string TruncateUrl(string url) =>
        string.IsNullOrEmpty(url) ? "(empty)"
        : url.Length <= 120 ? url
        : url.Substring(0, 120) + "…";

    public double PositionSeconds =>
        Interlocked.Read(ref samplesPlayed) / (double)(WaveFormat.SampleRate * WaveFormat.Channels);

    // === Seek ===============================================================

    /// <summary>
    /// Move the playhead to <paramref name="seconds"/>. Mode decision happens
    /// in <see cref="ApplyPendingSeekIfAny"/>: FileSeek if the cache covers
    /// the target byte offset (instant), otherwise UrlSeek (snappy fresh
    /// HTTP request with -ss). The download controller keeps running across
    /// the swap, so the cache continues to fill regardless of where the
    /// user seeked to.
    /// </summary>
    public void SeekToSeconds(double seconds)
    {
        if (disposed) return;
        var clamped = Math.Max(0, seconds);
        lock (swapLock)
        {
            pendingSeekSeconds = clamped;
        }
        Interlocked.Exchange(ref samplesPlayed,
            (long)(clamped * WaveFormat.SampleRate * WaveFormat.Channels));
        // Kill the current playback ffmpeg so any in-flight or about-to-block
        // Read returns 0 quickly — subsequent Read picks up the pending seek.
        try
        {
            if (!ffmpegProc.HasExited)
            {
                killedByUs = true;
                ffmpegProc.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    private void ApplyPendingSeekIfAny()
    {
        double seekTo;
        lock (swapLock)
        {
            seekTo = pendingSeekSeconds;
            if (seekTo < 0) return;
            pendingSeekSeconds = -1;
        }

        try
        {
            try { if (!ffmpegProc.HasExited) ffmpegProc.Kill(entireProcessTree: true); }
            catch { }
            try { ffmpegProc.Dispose(); } catch { }

            // Tear the Live tee target down regardless of which mode we're
            // entering — if we're going to FileSeek/UrlSeek, the live ffmpeg
            // is being replaced; if some watchdog corner case puts us back
            // into Live, StartLivePipeline (not used here) would re-wire it.
            lock (teeLock) { liveFfmpegStdin = null; }

            var (newMode, newProc) = PickSeekModeAndSpawn(seekTo);
            killedByUs = false;
            // Reset NaturalExited's single-fire guard so the next ffmpeg's
            // genuine end-of-track still gets through.
            naturalExitFired = false;
            InstallPlaybackProc(newProc, newMode, ffmpegStdinForTee: null);

            Interlocked.Exchange(ref samplesPlayed,
                (long)(seekTo * WaveFormat.SampleRate * WaveFormat.Channels));
            Plugin.Log.Info(
                $"[ffmpeg] seek applied. mode={newMode} target={seekTo:F1}s " +
                $"cacheBytes={Interlocked.Read(ref tempFileLength)} " +
                $"targetByteEst={(long)(seekTo * bytesPerSec)}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Subprocess audio seek failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronously tear down the current playback ffmpeg and replace it
    /// with a fresh UrlSeek-mode ffmpeg at <paramref name="startSec"/>. Used
    /// by the Read loop's "FileSeek outran the cache" recovery, which must
    /// not route through PickSeekModeAndSpawn (the bitrate-based mode picker)
    /// because that could re-select FileSeek and loop. Runs on the audio
    /// thread, so it inherits the same "no in-flight Read on this instance"
    /// guarantee as ApplyPendingSeekIfAny.
    /// </summary>
    private void SwitchToUrlSeek(double startSec)
    {
        try
        {
            try { if (!ffmpegProc.HasExited) ffmpegProc.Kill(entireProcessTree: true); }
            catch { }
            try { ffmpegProc.Dispose(); } catch { }
            lock (teeLock) { liveFfmpegStdin = null; }
            var newProc = SpawnFfmpegUrl(startSec);
            killedByUs = false;
            naturalExitFired = false;
            InstallPlaybackProc(newProc, PlaybackMode.UrlSeek, ffmpegStdinForTee: null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"SwitchToUrlSeek failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pick FileSeek vs UrlSeek based on download progress. Uses a 0.95
    /// safety margin against the estimated byte offset since <c>bytesPerSec</c>
    /// is an over-estimate by design — without the margin we'd occasionally
    /// hand ffmpeg a file position past its actual end and get an immediate
    /// EOF instead of audio.
    /// </summary>
    private (PlaybackMode, Process) PickSeekModeAndSpawn(double seekTo)
    {
        var cached = Interlocked.Read(ref tempFileLength);
        var targetBytes = (long)(seekTo * bytesPerSec);
        // Once download is complete, FileSeek can always reach any in-bounds
        // target. Until then, require the cache head to be comfortably past
        // the estimated target.
        var fileOk = downloadComplete
            || (cached > 0 && targetBytes < cached * 0.95);
        if (fileOk)
        {
            return (PlaybackMode.FileSeek, SpawnFfmpegFile(seekTo));
        }
        return (PlaybackMode.UrlSeek, SpawnFfmpegUrl(seekTo));
    }

    // === Lifecycle ==========================================================

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        try { watchdogCts.Cancel(); } catch { }
        try { watchdogTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        try { watchdogCts.Dispose(); } catch { }

        // Cancel and tear down the download controller first so the tee pump
        // exits before we close the underlying tempFile stream.
        try { downloadCts.Cancel(); } catch { }
        try
        {
            if (ytdlpProc is { } yp && !yp.HasExited)
                yp.Kill(entireProcessTree: true);
        }
        catch { }
        try { teePumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { ytdlpProc?.Dispose(); } catch { }
        try { tempFileStream?.Dispose(); } catch { }
        try { downloadCts.Dispose(); } catch { }

        // Kill the playback ffmpeg.
        try
        {
            if (!ffmpegProc.HasExited)
            {
                killedByUs = true;
                ffmpegProc.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try { ffmpegProc.Dispose(); } catch { }

        // Best-effort temp file cleanup. If this fails (locked, permission),
        // Windows will eventually clear the temp directory; the GUID name
        // guarantees we never collide with another run.
        try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); }
        catch (Exception ex)
        {
            Plugin.Log.Info($"[tempfile] cleanup deferred: {ex.Message}");
        }
    }

    private void OnFfmpegExited(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, ffmpegProc)) return;
        if (killedByUs || disposed) return;
        if (naturalExitFired) return;
        naturalExitFired = true;
        try { NaturalExited?.Invoke(); }
        catch (Exception ex) { Plugin.Log.Warning($"NaturalExited handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// True if ffmpeg ran to completion on its own (exit code 0) — i.e. the
    /// input stream reached a real EOF. The Live-mode case requires both the
    /// download controller AND the playback ffmpeg to have finished cleanly;
    /// a half-downloaded file masquerading as "playback ended" would lie to
    /// the auto-loop logic about whether the track really finished.
    /// </summary>
    public bool DidExitCleanly()
    {
        if (killedByUs) return false;
        try
        {
            if (!ffmpegProc.HasExited) return false;
            if (ffmpegProc.ExitCode != 0) return false;
            // FileSeek / UrlSeek may exit cleanly when their seekable input
            // hits EOF — for File this means the cache file, for Url the
            // CDN. Both are accurate "track ended" signals.
            // Live mode: the playback ffmpeg only sees EOF when its stdin
            // closes, which happens when the tee pump finishes draining
            // yt-dlp. If yt-dlp errored partway through, downloadFailed is
            // set and we must NOT treat the playback EOF as a clean end.
            if (mode == PlaybackMode.Live && downloadFailed) return false;
            return true;
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
        var psi = binaries.BuildYtDlpStartInfo(url, playlistRandom, cookiesFromBrowser);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

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

        try { proc.Kill(entireProcessTree: true); } catch { }

        if (string.IsNullOrEmpty(firstLine))
        {
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
