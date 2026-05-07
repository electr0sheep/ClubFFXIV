using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClubFFXIV.Audio;

/// <summary>
/// One voice in the multi-stream mixer. Wraps a single source decoder, format-
/// adapts it to the mixer's required format (mono→stereo + resample as needed),
/// and exposes the spatial filter + per-voice volume stage. The mixer reads
/// from this voice's volume stage like it reads from any ISampleProvider input.
///
/// Lifecycle: constructed after the source has finished its async startup, so
/// the audio thread sees no half-built voices. Disposing tears down the source.
/// DEBUG-only — Release builds keep using the singleton StreamPlayer.
/// </summary>
internal sealed class StreamVoice : ISampleProvider, IDisposable
{
    public string CanonicalKey { get; }
    public string Url { get; }
    public WaveFormat WaveFormat => volumeStage.WaveFormat;

    /// <summary>
    /// The underlying source's clean-exit oracle, if it implements one.
    /// MultiStreamPlayer reads this from its MixerInputEnded handler to
    /// distinguish a natural EOF (eligible for auto-loop) from a disposal-
    /// driven removal (user stop, walked out of range, evicted by the cap).
    /// Null for sources that don't implement <see cref="ICleanExitSource"/>
    /// (currently HttpAudioReader — direct HTTP streams are indefinite by
    /// design, so they never naturally end).
    /// </summary>
    public ICleanExitSource? CleanExit { get; }

    private readonly IDisposable sourceDisposable;
    private readonly BiQuadFilterSampleProvider lowpass;
    private readonly VolumeSampleProvider volumeStage;
    private bool disposed;

    public StreamVoice(
        string canonicalKey,
        string url,
        ISampleProvider source,
        IDisposable sourceDisposable,
        WaveFormat targetFormat,
        float initialCutoffHz)
    {
        CanonicalKey = canonicalKey;
        Url = url;
        this.sourceDisposable = sourceDisposable;
        CleanExit = source as ICleanExitSource;

        ISampleProvider chain = source;

        // Mono → stereo upmix first so the resampler operates on the final
        // channel count. The reverse order would resample twice as many samples.
        if (chain.WaveFormat.Channels == 1 && targetFormat.Channels == 2)
        {
            chain = new MonoToStereoSampleProvider(chain);
        }
        if (chain.WaveFormat.SampleRate != targetFormat.SampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, targetFormat.SampleRate);
        }

        lowpass = new BiQuadFilterSampleProvider(chain, initialCutoffHz);
        volumeStage = new VolumeSampleProvider(lowpass) { Volume = 0f };
    }

    public int Read(float[] buffer, int offset, int count) =>
        volumeStage.Read(buffer, offset, count);

    public float Volume
    {
        get => volumeStage.Volume;
        set => volumeStage.Volume = value;
    }

    public float CutoffHz
    {
        get => lowpass.CutoffHz;
        set => lowpass.CutoffHz = value;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { sourceDisposable.Dispose(); } catch { /* swallow during teardown */ }
    }
}
