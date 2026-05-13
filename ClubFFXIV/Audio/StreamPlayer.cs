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
    private StereoBalanceSampleProvider? balance;
    private VolumeSampleProvider? volumeStage;
    private WaveOutEvent? output;

    private float masterVolume = 0.7f;
    private float spatialVolume = 1f;
    private float spatialCutoff = BypassCutoffHz;
    private float spatialPan;
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

    /// <summary>
    /// Stereo balance, -1..+1. Driven by the proximity tick from the door's
    /// listener-relative angle; 0 when directional audio is disabled.
    /// </summary>
    public float SpatialPan
    {
        get => spatialPan;
        set
        {
            spatialPan = Math.Clamp(value, -1f, 1f);
            if (balance != null) balance.Pan = spatialPan;
        }
    }

    public void SetSpatial(float volume, float cutoffHz, float pan = 0f)
    {
        SpatialVolume = volume;
        SpatialCutoffHz = cutoffHz;
        SpatialPan = pan;
    }

    public void BypassSpatial()
    {
        SpatialVolume = 1f;
        SpatialCutoffHz = BypassCutoffHz;
        SpatialPan = 0f;
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
            Plugin.Log.Info($"[player] AutoMuted={value} url={currentUrl ?? "(none)"}");
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
            Plugin.Log.Info($"[player] UserMuted={value} url={currentUrl ?? "(none)"}");
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

        // Binaries are no longer auto-downloaded — installing them is an
        // explicit step in the setup wizard / /pclub config. Surface a clear,
        // actionable error if the user hits a yt-dlp URL without installing.
        var missing = AudioSourceFactory.DescribeMissingBinaries(url, binaryManager);
        if (missing != null)
        {
            throw new InvalidOperationException(
                $"This stream type needs {missing}, which hasn't been downloaded. " +
                $"Open /pclub config → External binaries to install (~83 MB total).");
        }

        var (newSource, newDisposable) = await AudioSourceFactory.CreateAsync(
            url, binaryManager, PlaylistRandom, YtDlpCookiesBrowser, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            newDisposable.Dispose();
            return;
        }

        var initialPan = spatialPan;
        var built = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var lp = new BiQuadFilterSampleProvider(newSource, initialCutoff);
            // The single-stream chain doesn't pre-upmix mono → stereo (the
            // WaveOutEvent will adapt at output), so guard the balance stage
            // behind a stereo check. Mono streams skip directional pan, which
            // is the right answer anyway: a mono source has no L/R to bias.
            ISampleProvider chain = lp;
            StereoBalanceSampleProvider? bal = null;
            if (newSource.WaveFormat.Channels == 2)
            {
                bal = new StereoBalanceSampleProvider(lp, initialPan);
                chain = bal;
            }
            var v = new VolumeSampleProvider(chain) { Volume = 0f };
            var o = new WaveOutEvent();
            o.Init(v.ToWaveProvider());
            return new BuiltChain(newSource, newDisposable, lp, bal, v, o);
        }, ct).ConfigureAwait(false);

        // Capture the URL + source for the natural-end check. Closure refs
        // per-call locals (not class fields) so the right URL fires even if
        // a later Play() reassigns this.currentUrl.
        //
        // Both the DidExitCleanly check and the subscriber invoke run
        // SYNCHRONOUSLY on the audio thread. Two reasons:
        //   1. DidExitCleanly: we'd otherwise race our own framework
        //      auto-resume — the tick sees IsPlaying=false right after
        //      PlaybackStopped and starts a new PlayAsync, whose Stop()
        //      disposes this source, so a deferred check would read a
        //      closed handle and return false.
        //   2. The invoke itself: subscribers (Plugin.OnStreamNaturallyEnded)
        //      need to set pendingStartUrl synchronously so the next
        //      framework tick sees streamWillPlay=true. If we Task.Run the
        //      invoke, a tick can land in the gap between IsPlaying flipping
        //      false and StartStreamAsync setting pendingStartUrl, and
        //      ApplyAudioPolicy unmutes the game's BGM mid-loop.
        // Subscribers must keep their handlers fast and non-blocking
        // (Plugin.OnStreamNaturallyEnded fires-and-forgets StartStreamAsync,
        // which yields off the audio thread immediately).
        var endedUrl = url;
        var endedSource = newSource;
        built.Output.PlaybackStopped += (_, _) =>
        {
            // ICleanExitSource covers both single-stream (SubprocessAudioReader)
            // and playlist (PlaylistAudioReader) sources uniformly.
            if (endedSource is ICleanExitSource clean && clean.DidExitCleanly())
            {
                StreamNaturallyEnded?.Invoke(endedUrl);
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
        balance = built.Balance;
        volumeStage = built.Volume;
        output = built.Output;
        currentUrl = url;

        lowpass.CutoffHz = spatialCutoff;
        if (balance != null) balance.Pan = spatialPan;
        volumeStage.Volume = EffectiveVolume();

        output.Play();
    }

    public void Play(string url) => PlayAsync(url).GetAwaiter().GetResult();

    private readonly record struct BuiltChain(
        ISampleProvider Source,
        IDisposable SourceDisposable,
        BiQuadFilterSampleProvider Lowpass,
        StereoBalanceSampleProvider? Balance,
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
        Plugin.Log.Info(
            $"[player] Pause requested. state={output?.PlaybackState.ToString() ?? "(no-output)"} " +
            $"url={currentUrl ?? "(none)"}");
        try { output?.Pause(); } catch { /* device gone */ }
    }

    /// <summary>
    /// Resume from a paused state. Calling on an already-playing or stopped
    /// device is a no-op (NAudio's WaveOutEvent.Play() is idempotent during
    /// Playing, and there's nothing to resume when stopped).
    /// </summary>
    public void Resume()
    {
        Plugin.Log.Info(
            $"[player] Resume requested. state={output?.PlaybackState.ToString() ?? "(no-output)"} " +
            $"url={currentUrl ?? "(none)"}");
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
        Plugin.Log.Info(
            $"[player] SkipToNext requested. skippable={source is ISkippableSource} " +
            $"posSec={PositionSeconds:F1} url={currentUrl ?? "(none)"}");
        if (source is ISkippableSource s) s.SkipToNext();
    }

    public void Stop()
    {
        if (output != null || source != null)
        {
            Plugin.Log.Info(
                $"[player] Stop. state={output?.PlaybackState.ToString() ?? "(no-output)"} " +
                $"posSec={PositionSeconds:F1} url={currentUrl ?? "(none)"}");
        }
        try { output?.Stop(); } catch { /* swallow during teardown */ }
        output?.Dispose();
        sourceDisposable?.Dispose();
        output = null;
        source = null;
        sourceDisposable = null;
        lowpass = null;
        balance = null;
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
