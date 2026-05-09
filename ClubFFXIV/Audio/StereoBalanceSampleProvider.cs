using System;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Stereo-in, stereo-out balance stage. Live-tunable Pan in [-1, +1]:
/// 0 leaves the source untouched, -1 silences the right channel, +1
/// silences the left.
///
/// Why "balance" and not constant-power panning of a mono-summed source:
/// at pan=0 we want the music's stereo image preserved (kick centered,
/// hat slightly right, etc.) — exactly what the source already encodes.
/// Mono-summing then re-spreading would collapse that image even when
/// the door is directly in front. Tradeoff: at the extreme |pan|=1 you
/// only hear one channel of the source, so a track with mix elements
/// hard-panned to the silenced side temporarily disappears. In practice
/// |pan| rarely reaches 1 (only when a club is exactly perpendicular to
/// the listener's facing) and most music keeps the energy near center.
/// </summary>
public sealed class StereoBalanceSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private float pan;
    // Last pan value actually applied to the audio (i.e. where the previous
    // buffer ended). Each Read interpolates from here to `pan` across the
    // current buffer, so per-tick pan updates from the proximity loop don't
    // produce audible step changes inside a 150ms WaveOutEvent buffer.
    private float appliedPan;

    public WaveFormat WaveFormat => source.WaveFormat;

    public float Pan
    {
        get => pan;
        set => pan = Math.Clamp(value, -1f, 1f);
    }

    public StereoBalanceSampleProvider(ISampleProvider source, float initialPan = 0f)
    {
        if (source.WaveFormat.Channels != 2)
            throw new ArgumentException(
                "StereoBalanceSampleProvider requires a stereo source.",
                nameof(source));
        this.source = source;
        this.pan = Math.Clamp(initialPan, -1f, 1f);
        this.appliedPan = this.pan;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        // Snapshot the target so a SetSpatial call that lands mid-Read doesn't
        // re-aim the interpolation in the middle of the buffer. Whatever value
        // is set after the snapshot will be picked up at the next Read.
        var target = pan;
        var start = appliedPan;
        appliedPan = target;

        // Hot path: pan hasn't changed and is dead-center → no work needed.
        if (start == 0f && target == 0f) return read;

        var frames = read / 2;
        if (frames <= 0) return read;

        // When the pan hasn't moved between buffers, fall through to a single
        // pair of gain coefficients applied uniformly — avoids the per-frame
        // arithmetic on a stationary listener.
        if (start == target)
        {
            ApplyConstantBalance(buffer, offset, frames, target);
            return read;
        }

        // Linearly interpolate pan from `start` to `target` across the buffer.
        // Linear (rather than constant-power) is fine because adjacent buffers
        // are very close in pan value, so the per-sample gain changes are tiny
        // — no zipper noise even on aggressive camera spins.
        var step = (target - start) / frames;
        var current = start;
        for (int f = 0; f < frames; f++)
        {
            var gainL = current <= 0f ? 1f : 1f - current;
            var gainR = current >= 0f ? 1f : 1f + current;
            var i = offset + (f << 1);
            buffer[i] *= gainL;
            buffer[i + 1] *= gainR;
            current += step;
        }
        return read;
    }

    private static void ApplyConstantBalance(float[] buffer, int offset, int frames, float p)
    {
        if (p == 0f) return;
        var gainL = p <= 0f ? 1f : 1f - p;
        var gainR = p >= 0f ? 1f : 1f + p;
        for (int f = 0; f < frames; f++)
        {
            var i = offset + (f << 1);
            buffer[i] *= gainL;
            buffer[i + 1] *= gainR;
        }
    }
}
