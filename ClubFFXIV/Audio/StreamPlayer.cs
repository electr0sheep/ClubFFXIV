using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClubFFXIV.Audio;

/// <summary>
/// Plays an audio stream through a tunable spatial chain:
///   source → BiQuadLowpass → VolumeSampleProvider → WaveOutEvent.
///
/// The source is dispatched by URL: direct HTTP MP3/OGG goes through
/// HttpAudioReader (in-process NLayer/NVorbis), Twitch/YouTube/etc. go
/// through SubprocessAudioReader (yt-dlp + ffmpeg).
/// </summary>
public sealed class StreamPlayer : IDisposable
{
    private const float BypassCutoffHz = 20000f;

    private readonly BinaryManager binaryManager;

    private ISampleProvider? source;
    private IDisposable? sourceDisposable;
    private BiQuadFilterSampleProvider? lowpass;
    private VolumeSampleProvider? volumeStage;
    private WaveOutEvent? output;

    private float masterVolume = 0.7f;
    private float spatialVolume = 1f;
    private float spatialCutoff = BypassCutoffHz;
    // Two independent mute inputs: autoMuted is set on focus / FFXIV-config
    // transitions by the focus-mute policy; userMuted is the explicit toggle
    // from the Now Playing header. They OR together — either one zeros output.
    private bool autoMuted;
    private bool userMuted;
    private string? currentUrl;

    /// <summary>
    /// Forwarded to yt-dlp as --playlist-random when true, --lazy-playlist
    /// when false. Pushed by <see cref="Plugin.SyncYtDlpOptions"/> when the
    /// user toggles it; the value is read at the next <see cref="PlayAsync"/>
    /// call, so toggles apply on the following start without interrupting
    /// anything currently playing.
    /// </summary>
    public bool PlaylistRandom { get; set; }

    /// <summary>
    /// Browser name passed to yt-dlp's --cookies-from-browser, or empty to
    /// disable. Pushed by <see cref="Plugin.SyncYtDlpOptions"/> when the user
    /// edits it; read at the next <see cref="PlayAsync"/> call.
    /// </summary>
    public string YtDlpCookiesBrowser { get; set; } = "";

    public StreamPlayer(BinaryManager binaryManager)
    {
        this.binaryManager = binaryManager;
    }

    public bool IsPlaying => output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => output?.PlaybackState == PlaybackState.Paused;
    /// <summary>
    /// True if there's an active source (whether currently playing or paused).
    /// Used by the Now Playing header so a paused row stays visible — without
    /// this, IsPlaying alone would hide the only thing the user can click to
    /// resume.
    /// </summary>
    public bool IsActive => IsPlaying || IsPaused;
    public string? CurrentUrl => currentUrl;

    /// <summary>
    /// True iff the current source is a yt-dlp live channel / Icecast / other
    /// indefinite stream (or no source set). Drives the Now Playing transport
    /// gate — live rows skip play/pause/seek and keep mute-only chrome.
    /// </summary>
    public bool IsLive =>
        currentUrl is null || LiveStatusCache.IsLive(currentUrl);

    /// <summary>
    /// Total duration of the current source in seconds, or 0 if unknown
    /// (live, or a non-live source that didn't expose duration metadata).
    /// </summary>
    public double DurationSeconds =>
        currentUrl is null ? 0 : DurationCache.Get(currentUrl);

    /// <summary>
    /// Elapsed audio seconds since playback started or the last seek. Reads
    /// through the underlying source's playhead counter — paused streams hold
    /// at the last value, seeked streams jump to the new target immediately.
    /// Returns 0 when the source isn't seekable (live).
    /// </summary>
    public double PositionSeconds =>
        source is ISeekableSource s ? s.PositionSeconds : 0;

    /// <summary>
    /// Fires when a finite source (currently only yt-dlp / ffmpeg-backed
    /// streams) reaches a clean EOF — i.e. the video finished playing
    /// rather than failing on a network/decoder error. Argument is the URL
    /// that just ended. Subscribers decide whether to re-play it (loop)
    /// or let the playback stay stopped. Does not fire for indefinite
    /// sources (Twitch live, Icecast) or for user-initiated stops.
    /// </summary>
    public event Action<string>? StreamNaturallyEnded;

    public float MasterVolume
    {
        get => masterVolume;
        set
        {
            masterVolume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public float SpatialVolume
    {
        get => spatialVolume;
        set
        {
            spatialVolume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public float SpatialCutoffHz
    {
        get => spatialCutoff;
        set
        {
            spatialCutoff = value;
            if (lowpass != null) lowpass.CutoffHz = value;
        }
    }

    public void SetSpatial(float volume, float cutoffHz)
    {
        SpatialVolume = volume;
        SpatialCutoffHz = cutoffHz;
    }

    public void BypassSpatial()
    {
        SpatialVolume = 1f;
        SpatialCutoffHz = BypassCutoffHz;
    }

    /// <summary>
    /// Auto-mute set by the framework tick when the FFXIV window loses focus.
    /// Composes with <see cref="UserMuted"/>: either one zeros output.
    /// </summary>
    public bool AutoMuted
    {
        get => autoMuted;
        set
        {
            if (autoMuted == value) return;
            autoMuted = value;
            ApplyVolume();
        }
    }

    /// <summary>
    /// Explicit user toggle (Mute button in the Now Playing header). Survives
    /// framework ticks — the auto-mute path uses <see cref="AutoMuted"/>.
    /// </summary>
    public bool UserMuted
    {
        get => userMuted;
        set
        {
            if (userMuted == value) return;
            userMuted = value;
            ApplyVolume();
        }
    }

    /// <summary>
    /// Build the chain off the framework thread. Source construction blocks
    /// while the network buffer fills (1–3s for direct HTTP, ~5s for subprocess
    /// pipeline cold-start).
    /// </summary>
    public async Task PlayAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is empty", nameof(url));

        var initialCutoff = spatialCutoff;
        var kind = UrlClassifier.ClassifyUrl(url);

        // Build the source. Different code paths for direct HTTP vs subprocess.
        ISampleProvider newSource;
        IDisposable newDisposable;

        if (kind == AudioSourceKind.YtDlp)
        {
            // Binaries are no longer auto-downloaded — installing them is an
            // explicit step in the setup wizard / /pclub config. Surface a
            // clear, actionable error if the user hits a yt-dlp URL without
            // installing first.
            if (!binaryManager.Ready)
            {
                var missing = !binaryManager.YtDlpInstalled && !binaryManager.FfmpegInstalled
                    ? "yt-dlp + ffmpeg"
                    : !binaryManager.YtDlpInstalled
                        ? "yt-dlp"
                        : "ffmpeg";
                throw new InvalidOperationException(
                    $"This stream type needs {missing}, which hasn't been downloaded. " +
                    $"Open /pclub config → External binaries to install (~83 MB total).");
            }
            // PlaylistAudioReader holds the long-lived yt-dlp + iterates
            // ffmpeg invocations as items end. For single-video URLs it
            // degenerates to one ffmpeg + a clean EOF, just like the old
            // single-shot path.
            var sub = await PlaylistAudioReader.CreateAsync(
                url, binaryManager, PlaylistRandom, YtDlpCookiesBrowser, ct).ConfigureAwait(false);
            newSource = sub;
            newDisposable = sub;
        }
        else
        {
            var http = await HttpAudioReader.CreateAsync(url, ct).ConfigureAwait(false);
            newSource = http;
            newDisposable = http;
        }

        if (ct.IsCancellationRequested)
        {
            newDisposable.Dispose();
            return;
        }

        var built = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var lp = new BiQuadFilterSampleProvider(newSource, initialCutoff);
            var v = new VolumeSampleProvider(lp) { Volume = 0f };
            var o = new WaveOutEvent();
            o.Init(v.ToWaveProvider());
            return new BuiltChain(newSource, newDisposable, lp, v, o);
        }, ct).ConfigureAwait(false);

        // Capture the URL + source for the natural-end check. Closure refs
        // per-call locals (not class fields) so the right URL fires even if
        // a later Play() reassigns this.currentUrl.
        //
        // DidExitCleanly is called SYNCHRONOUSLY here on the audio thread
        // because we'd otherwise race our own framework auto-resume: the
        // framework tick sees IsPlaying=false right after PlaybackStopped
        // and starts a new PlayAsync, whose Stop() disposes this source —
        // any Task.Run-deferred check would then read a closed handle and
        // return false, suppressing the natural-end event. The check itself
        // is non-blocking now (no WaitForExit), so doing it on the audio
        // thread is fine. Only the subscriber callback runs in Task.Run.
        var endedUrl = url;
        var endedSource = newSource;
        built.Output.PlaybackStopped += (_, _) =>
        {
            // ICleanExitSource covers both single-stream (SubprocessAudioReader)
            // and playlist (PlaylistAudioReader) sources uniformly.
            if (endedSource is ICleanExitSource clean && clean.DidExitCleanly())
            {
                var u = endedUrl;
                _ = Task.Run(() => StreamNaturallyEnded?.Invoke(u));
            }
        };

        if (ct.IsCancellationRequested)
        {
            built.Dispose();
            return;
        }

        Stop();

        source = built.Source;
        sourceDisposable = built.SourceDisposable;
        lowpass = built.Lowpass;
        volumeStage = built.Volume;
        output = built.Output;
        currentUrl = url;

        lowpass.CutoffHz = spatialCutoff;
        volumeStage.Volume = EffectiveVolume();

        output.Play();
    }

    public void Play(string url) => PlayAsync(url).GetAwaiter().GetResult();

    private readonly record struct BuiltChain(
        ISampleProvider Source,
        IDisposable SourceDisposable,
        BiQuadFilterSampleProvider Lowpass,
        VolumeSampleProvider Volume,
        WaveOutEvent Output) : IDisposable
    {
        public void Dispose()
        {
            try { Output.Dispose(); } catch { /* ignore */ }
            try { SourceDisposable.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Pause the WaveOut device. Source decoders (ffmpeg pipe, in-process
    /// MP3/OGG) back-pressure naturally when our Read stops draining, so no
    /// extra plumbing is needed at the source layer. No-op if there's no
    /// active output or it's already paused.
    /// </summary>
    public void Pause()
    {
        try { output?.Pause(); } catch { /* device gone */ }
    }

    /// <summary>
    /// Resume from a paused state. Calling on an already-playing or stopped
    /// device is a no-op (NAudio's WaveOutEvent.Play() is idempotent during
    /// Playing, and there's nothing to resume when stopped).
    /// </summary>
    public void Resume()
    {
        if (output?.PlaybackState == PlaybackState.Paused)
        {
            try { output.Play(); } catch { /* device gone */ }
        }
    }

    /// <summary>
    /// Move the playhead. Forwards to the source if it implements
    /// <see cref="ISeekableSource"/> (currently ffmpeg-backed). No-op for
    /// live / non-seekable sources, so the UI can call without first
    /// re-checking <see cref="IsLive"/>.
    /// </summary>
    public void SeekToSeconds(double seconds)
    {
        if (source is ISeekableSource s) s.SeekToSeconds(seconds);
    }

    /// <summary>
    /// True iff the current source supports skipping to a next item — i.e.
    /// it's a yt-dlp-backed playlist wrapper. Single-video URLs still go
    /// through the playlist wrapper (1-item playlist), so this stays true
    /// for them; calling skip on a 1-item playlist exhausts it, and the
    /// natural-end loop logic handles the resulting behavior.
    /// </summary>
    public bool IsSkippable => source is ISkippableSource;

    /// <summary>
    /// Jump to the next playlist item. No-op when the source can't skip
    /// (live HTTP / Icecast); UI can call without first checking IsLive.
    /// </summary>
    public void SkipToNext()
    {
        if (source is ISkippableSource s) s.SkipToNext();
    }

    public void Stop()
    {
        try { output?.Stop(); } catch { /* swallow during teardown */ }
        output?.Dispose();
        sourceDisposable?.Dispose();
        output = null;
        source = null;
        sourceDisposable = null;
        lowpass = null;
        volumeStage = null;
        currentUrl = null;
    }

    public void Dispose() => Stop();

    private float EffectiveVolume() =>
        (autoMuted || userMuted) ? 0f : (masterVolume * spatialVolume);

    private void ApplyVolume()
    {
        if (volumeStage != null) volumeStage.Volume = EffectiveVolume();
    }
}
