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
    private bool muted;
    private string? currentUrl;

    public StreamPlayer(BinaryManager binaryManager)
    {
        this.binaryManager = binaryManager;
    }

    public bool IsPlaying => output?.PlaybackState == PlaybackState.Playing;
    public string? CurrentUrl => currentUrl;

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

    public bool Muted
    {
        get => muted;
        set
        {
            if (muted == value) return;
            muted = value;
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
            // Need ffmpeg + yt-dlp on disk first. EnsureInstalledAsync is a
            // no-op once binaries are cached; first call may take ~30s as it
            // downloads ~80MB.
            await binaryManager.EnsureInstalledAsync(ct: ct).ConfigureAwait(false);
            var sub = await SubprocessAudioReader.CreateAsync(url, binaryManager, ct).ConfigureAwait(false);
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

    private float EffectiveVolume() => muted ? 0f : (masterVolume * spatialVolume);

    private void ApplyVolume()
    {
        if (volumeStage != null) volumeStage.Volume = EffectiveVolume();
    }
}
