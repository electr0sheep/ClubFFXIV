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
    private readonly StereoBalanceSampleProvider balance;
    private readonly VolumeSampleProvider volumeStage;
    // Captured at construction so per-voice transport (pause/seek/position)
    // can address the same playhead the mixer reads through. null for
    // non-seekable sources (HTTP Icecast); the public Seek/Position methods
    // no-op in that case so the UI can call without first checking IsLive.
    private readonly ISeekableSource? seekable;
    private readonly ISkippableSource? skippable;
    private bool paused;
    private bool disposed;
    // Latched once the source has signalled true EOF (Read returned 0).
    // Read returns 0 from then on so MixingSampleProvider drops us via the
    // < count branch in OnMixerInputEnded — see Read() for full rationale.
    private bool sourceEofReached;

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
        // PrebufferedSampleProvider always implements the three optional
        // interfaces (so its seek/skip can flush the ring before forwarding),
        // so its Supports* flags — not an `as` cast — are the source of truth
        // for whether the *inner* source actually supports each capability.
        if (source is PrebufferedSampleProvider buffered)
        {
            CleanExit = buffered.SupportsCleanExit ? buffered : null;
            seekable = buffered.SupportsSeek ? buffered : null;
            skippable = buffered.SupportsSkip ? buffered : null;
        }
        else
        {
            CleanExit = source as ICleanExitSource;
            seekable = source as ISeekableSource;
            skippable = source as ISkippableSource;
        }

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
        // Balance lives between the lowpass and the volume stage so master/
        // mute scaling still applies on top of the directional pan, and so
        // the lowpass operates on the unbalanced signal (cleaner filter math).
        balance = new StereoBalanceSampleProvider(lowpass);
        volumeStage = new VolumeSampleProvider(balance) { Volume = 0f };
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Paused: emit silence WITHOUT advancing the source chain. The
        // ffmpeg-backed reader's stdout pipe back-pressures and the
        // subprocess blocks on write — same shape as WaveOutEvent.Pause()
        // does for the single-stream player. Returning `count` (not 0) keeps
        // the mixer treating us as a still-live input rather than firing
        // MixerInputEnded. Position counter holds at the last value because
        // no Read happens on the source while paused.
        if (paused)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        // Already saw the source's true EOF — propagate by returning 0 so
        // MixingSampleProvider drops us and fires OnMixerInputEnded (the
        // path that triggers Plugin.OnMultiStreamVoiceNaturallyEnded /
        // loop-or-suppress logic).
        if (sourceEofReached) return 0;

        var n = volumeStage.Read(buffer, offset, count);
        if (n == count) return n;

        if (n == 0)
        {
            // Genuine EOF (PlaylistAudioReader exhausted, HTTP file ended,
            // etc.). Latch and propagate so the mixer drops us.
            sourceEofReached = true;
            return 0;
        }

        // Partial read — pad the remainder with silence and return `count`
        // so MixingSampleProvider does NOT interpret this as input-ended.
        // Without this, ffmpeg's last bytes at the end of every playlist
        // track (PlaylistAudioReader → SubprocessAudioReader's final pipe
        // drain) would tear our voice down mid-playlist: the inter-song gap
        // would briefly show no voices, ApplyAudioPolicy would unmute the
        // game's BGM, and HandleIndoorMode would re-add a fresh voice
        // (restarting the playlist from track 1). The next Read picks up
        // the source's silence-bridge / next-track path normally.
        Array.Clear(buffer, offset + n, count - n);
        return count;
    }

    /// <summary>
    /// Per-voice pause gate. Composes additively with mute (volume=0):
    /// muting is "still consume the stream, but at zero amplitude"; pausing
    /// is "stop consuming entirely". For ffmpeg-backed sources, pausing
    /// halts the subprocess via stdout back-pressure, so a paused voice
    /// stops costing CPU/network beyond the idle subprocess.
    /// </summary>
    public bool Paused
    {
        get => paused;
        set => paused = value;
    }

    /// <summary>
    /// True if this voice's source supports seeking. Drives the Now Playing
    /// UI's seek-bar gate alongside the live-status check; non-seekable
    /// sources (currently only HTTP Icecast) get pause-only chrome.
    /// </summary>
    public bool IsSeekable => seekable != null;

    /// <summary>Forward to the underlying source's playhead, or 0 if unseekable.</summary>
    public double PositionSeconds => seekable?.PositionSeconds ?? 0;

    /// <summary>Forward to the underlying source's seek; no-op if unseekable.</summary>
    public void SeekToSeconds(double seconds) => seekable?.SeekToSeconds(seconds);

    /// <summary>True iff the underlying source supports skip-next (yt-dlp playlist wrapper).</summary>
    public bool IsSkippable => skippable != null;

    /// <summary>Jump to the next playlist item; no-op for non-skippable sources.</summary>
    public void SkipToNext() => skippable?.SkipToNext();

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

    /// <summary>
    /// Stereo balance, -1 = full left, 0 = centered, +1 = full right.
    /// Driven by the proximity tick from the door's listener-relative angle.
    /// </summary>
    public float Pan
    {
        get => balance.Pan;
        set => balance.Pan = value;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { sourceDisposable.Dispose(); } catch { /* swallow during teardown */ }
    }
}
