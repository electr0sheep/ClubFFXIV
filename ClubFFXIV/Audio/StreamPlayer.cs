using System;
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

    public void Play(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is empty", nameof(url));

        Stop();

        reader = new MediaFoundationReader(url);
        var samples = reader.ToSampleProvider();
        lowpass = new BiQuadFilterSampleProvider(samples, spatialCutoff);
        volumeStage = new VolumeSampleProvider(lowpass) { Volume = EffectiveVolume() };

        output = new WaveOutEvent();
        output.Init(volumeStage.ToWaveProvider());
        output.Play();
        currentUrl = url;
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
