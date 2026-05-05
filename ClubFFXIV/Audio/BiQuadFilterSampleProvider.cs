using NAudio.Dsp;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Applies a single biquad lowpass filter per audio channel. Cutoff frequency is
/// live-tunable; coefficient updates are deferred to buffer boundaries to avoid
/// audible artifacts mid-buffer.
/// </summary>
public sealed class BiQuadFilterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly BiQuadFilter[] filters;
    private float cutoffHz;
    private float qFactor = 0.707f;
    private bool dirty = true;

    public WaveFormat WaveFormat => source.WaveFormat;

    public float CutoffHz
    {
        get => cutoffHz;
        set
        {
            if (cutoffHz == value) return;
            cutoffHz = value;
            dirty = true;
        }
    }

    public BiQuadFilterSampleProvider(ISampleProvider source, float initialCutoffHz)
    {
        this.source = source;
        this.cutoffHz = initialCutoffHz;
        this.filters = new BiQuadFilter[source.WaveFormat.Channels];
        RebuildFilters();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (dirty) RebuildFilters();
        var read = source.Read(buffer, offset, count);
        var channels = WaveFormat.Channels;
        for (int n = 0; n < read; n++)
        {
            var ch = n % channels;
            buffer[offset + n] = filters[ch].Transform(buffer[offset + n]);
        }
        return read;
    }

    private void RebuildFilters()
    {
        // Cap cutoff below Nyquist so the filter stays stable.
        var sr = WaveFormat.SampleRate;
        var safe = System.Math.Min(cutoffHz, sr * 0.45f);
        for (int i = 0; i < filters.Length; i++)
        {
            filters[i] = BiQuadFilter.LowPassFilter(sr, safe, qFactor);
        }
        dirty = false;
    }
}
