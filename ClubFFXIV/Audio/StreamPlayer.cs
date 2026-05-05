using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClubFFXIV.Audio;

/// <summary>
/// Plays an HTTP audio stream through a tunable spatial chain:
///   HttpAudioReader (NLayer/NVorbis) → BiQuadLowpass → VolumeSampleProvider → WaveOutEvent.
///
/// Spatial parameters (lowpass cutoff, spatial multiplier) are layered on top
/// of the user's master volume. When inside a house we set them to "transparent"
/// (cutoff = 20 kHz, spatial = 1.0) and the chain effectively passes audio unchanged.
///
/// Cross-platform decoder choice (NLayer/NVorbis) means this works under Wine
/// (XIVLauncher Mac/Linux), unlike NAudio's MediaFoundationReader.
/// </summary>
public sealed class StreamPlayer : IDisposable
{
    private const float BypassCutoffHz = 20000f;

    private HttpAudioReader? reader;
    private BiQuadFilterSampleProvider? lowpass;
    private VolumeSampleProvider? volumeStage;
    private WaveOutEvent? output;

    private float masterVolume = 0.7f;
    private float spatialVolume = 1f;
    private float spatialCutoff = BypassCutoffHz;
    private bool muted;
    private string? currentUrl;

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
    /// Build the chain off the framework thread. HttpAudioReader.CreateAsync blocks
    /// for ~1–3s waiting for the first decoded frame, depending on stream's initial
    /// buffer behaviour. Caller can cancel mid-build.
    /// </summary>
    public async Task PlayAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is empty", nameof(url));

        var initialCutoff = spatialCutoff;

        var newReader = await HttpAudioReader.CreateAsync(url, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            newReader.Dispose();
            return;
        }

        var built = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var lp = new BiQuadFilterSampleProvider(newReader, initialCutoff);
            var v = new VolumeSampleProvider(lp) { Volume = 0f };
            var o = new WaveOutEvent();
            o.Init(v.ToWaveProvider());
            return new BuiltChain(newReader, lp, v, o);
        }, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            built.Dispose();
            return;
        }

        Stop();

        reader = built.Reader;
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
        HttpAudioReader Reader,
        BiQuadFilterSampleProvider Lowpass,
        VolumeSampleProvider Volume,
        WaveOutEvent Output) : IDisposable
    {
        public void Dispose()
        {
            try { Output.Dispose(); } catch { /* ignore */ }
            try { Reader.Dispose(); } catch { /* ignore */ }
        }
    }

    public void Stop()
    {
        try { output?.Stop(); } catch { /* swallow during teardown */ }
        output?.Dispose();
        reader?.Dispose();
        output = null;
        reader = null;
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
