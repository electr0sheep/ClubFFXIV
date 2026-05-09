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
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        // Snapshot pan at the start of the buffer so live changes don't tear
        // gain coefficients mid-buffer. Same deferral pattern as the biquad
        // filter's `dirty` flag.
        var p = pan;
        if (p == 0f) return read; // hot path — most voices are centered most of the time
        var gainL = p <= 0f ? 1f : 1f - p;
        var gainR = p >= 0f ? 1f : 1f + p;
        // Stereo float samples are interleaved L,R,L,R,...; ignore any
        // trailing odd sample (shouldn't happen in practice — Read returns
        // even counts for stereo formats).
        for (int n = 0; n + 1 < read; n += 2)
        {
            buffer[offset + n] *= gainL;
            buffer[offset + n + 1] *= gainR;
        }
        return read;
    }
}
