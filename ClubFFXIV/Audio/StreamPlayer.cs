using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClubFFXIV.Audio;

/// <summary>
/// Plays an HTTP audio stream through a tunable spatial chain:
///   Source (MediaFoundationReader) → BiQuadLowpass → VolumeSampleProvider → WaveOutEvent.
///
/// Spatial parameters (lowpass cutoff, spatial multiplier) are layered on top
/// of the user's master volume. When inside a house we set them to "transparent"
/// (cutoff = 20 kHz, spatial = 1.0) and the chain effectively passes audio unchanged.
/// </summary>
public sealed class StreamPlayer : IDisposable
{
    private const float BypassCutoffHz = 20000f;

    private MediaFoundationReader? reader;
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

    /// <summary>User volume (config slider). Combines multiplicatively with spatial.</summary>
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

    /// <summary>Convenience setter — single update for both spatial knobs.</summary>
    public void SetSpatial(float volume, float cutoffHz)
    {
        SpatialVolume = volume;
        SpatialCutoffHz = cutoffHz;
    }

    /// <summary>Reset spatial chain to transparent (used when entering interior).</summary>
    public void BypassSpatial()
    {
        SpatialVolume = 1f;
        SpatialCutoffHz = BypassCutoffHz;
    }

    /// <summary>
    /// Hard mute that overrides master + spatial. Used when FFXIV is unfocused
    /// (so the stream tracks the game's mute-when-unfocused behavior).
    /// </summary>
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
    /// Build the chain off the framework thread (MediaFoundationReader blocks 1–3s
    /// on initial buffer for HTTP streams). Caller can cancel mid-build.
    /// On success, the new chain replaces the previous one and starts playing.
    /// </summary>
    public async Task PlayAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is empty", nameof(url));

        // Snapshot spatial state for chain construction. After the chain comes online,
        // any newer SpatialVolume/SpatialCutoffHz writes will be applied via ApplyVolume()
        // and the lowpass.CutoffHz setter — so live updates during loading still take effect.
        var initialCutoff = spatialCutoff;

        var built = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var r = new MediaFoundationReader(url);
            ct.ThrowIfCancellationRequested();
            var samples = r.ToSampleProvider();
            var lp = new BiQuadFilterSampleProvider(samples, initialCutoff);
            var v = new VolumeSampleProvider(lp) { Volume = 0f }; // start silent, ramped up below
            var o = new WaveOutEvent();
            o.Init(v.ToWaveProvider());
            return new BuiltChain(r, lp, v, o);
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

        // Apply latest live params (in case they changed during the build).
        lowpass.CutoffHz = spatialCutoff;
        volumeStage.Volume = EffectiveVolume();

        output.Play();
    }

    public void Play(string url) => PlayAsync(url).GetAwaiter().GetResult();

    private readonly record struct BuiltChain(
        MediaFoundationReader Reader,
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
