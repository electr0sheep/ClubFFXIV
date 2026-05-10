using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Wraps an audio source with a producer-thread-fed ring buffer so the audio
/// thread's <c>MixingSampleProvider.Read</c> never blocks on cold I/O — for
/// example a freshly-spawned ffmpeg subprocess whose stdout pipe hasn't yet
/// produced its first decoded samples.
///
/// Why this exists: the multi-stream mixer iterates every input sequentially
/// inside one Read call, so a newly-added voice with a cold pipe stalls the
/// whole mixer for hundreds of ms — audibly stuttering any voices that were
/// already playing happily. With this wrapper, the cold-start latency is
/// absorbed by a background producer task and never reaches the audio thread:
/// <see cref="PrimeAsync"/> waits for the ring to fill before the voice is
/// handed to the mixer; once live, underruns return silence (instead of
/// blocking) so the mixer keeps draining its own playback buffer.
///
/// Forwarding contract: this type always implements
/// <see cref="ICleanExitSource"/>, <see cref="ISeekableSource"/> and
/// <see cref="ISkippableSource"/>, but the methods no-op when the inner
/// source doesn't support them. Callers that need to gate UI on actual
/// support should consult <see cref="SupportsSeek"/>, <see cref="SupportsSkip"/>
/// and <see cref="SupportsCleanExit"/> rather than relying on <c>as</c> casts.
/// </summary>
internal sealed class PrebufferedSampleProvider :
    ISampleProvider, IDisposable, ICleanExitSource, ISeekableSource, ISkippableSource
{
    public WaveFormat WaveFormat { get; }
    public bool SupportsSeek { get; }
    public bool SupportsSkip { get; }
    public bool SupportsCleanExit { get; }

    private readonly ISampleProvider inner;
    private readonly IDisposable innerDisposable;
    private readonly ISeekableSource? innerSeekable;
    private readonly ISkippableSource? innerSkippable;
    private readonly ICleanExitSource? innerCleanExit;

    // Single-buffer ring; samples are interleaved exactly as the inner
    // source emits them (so this works for any channel count / sample rate).
    private readonly float[] ring;
    private readonly int ringCapacity;
    private int writePos;
    private int readPos;
    private int available;
    // Latched once the producer's inner.Read returns 0 — the inner's own
    // EOF semantics are authoritative (e.g. SubprocessAudioReader retries
    // internally on seek-induced zero-returns, so a 0 here is a real EOF).
    private bool eofReached;
    private bool disposed;

    private readonly object bufLock = new();
    private readonly CancellationTokenSource producerCts = new();
    private readonly Task producerTask;

    /// <summary>
    /// Constructs the wrapper and immediately spawns the background producer
    /// task. Caller typically follows up with <see cref="PrimeAsync"/> before
    /// adding the wrapper to a mixer; the wrapper takes ownership of
    /// <paramref name="innerDisposable"/> and disposes it when the wrapper is
    /// disposed.
    /// </summary>
    public PrebufferedSampleProvider(
        ISampleProvider inner,
        IDisposable innerDisposable,
        double bufferSeconds = 0.5)
    {
        this.inner = inner;
        this.innerDisposable = innerDisposable;
        WaveFormat = inner.WaveFormat;
        innerSeekable = inner as ISeekableSource;
        innerSkippable = inner as ISkippableSource;
        innerCleanExit = inner as ICleanExitSource;
        SupportsSeek = innerSeekable != null;
        SupportsSkip = innerSkippable != null;
        SupportsCleanExit = innerCleanExit != null;

        // Ring sized for the requested duration in interleaved samples; the
        // 4096-sample floor avoids degenerate sizes for very low-rate sources.
        var samples = (int)(bufferSeconds * WaveFormat.SampleRate * WaveFormat.Channels);
        ringCapacity = Math.Max(4096, samples);
        ring = new float[ringCapacity];

        producerTask = Task.Run(() => ProducerLoop(producerCts.Token));
    }

    /// <summary>
    /// Wait until the ring holds at least <paramref name="targetSeconds"/> of
    /// audio, the inner source EOFs, or <paramref name="ct"/> cancels. Caller
    /// should pass a target less than the constructor's <c>bufferSeconds</c>
    /// so the producer has headroom while priming. Returns immediately if the
    /// buffer is already at-or-above the target.
    /// </summary>
    public async Task PrimeAsync(double targetSeconds, CancellationToken ct)
    {
        var targetSamples = (int)(targetSeconds * WaveFormat.SampleRate * WaveFormat.Channels);
        targetSamples = Math.Min(targetSamples, ringCapacity);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (bufLock)
            {
                if (disposed) return;
                if (available >= targetSamples || eofReached) return;
            }
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    private void ProducerLoop(CancellationToken ct)
    {
        // Per-iteration scratch; sized to ~50ms so the producer doesn't burn
        // CPU on tiny reads but also doesn't sit on huge chunks while the
        // consumer is hungry.
        var producerChunk = Math.Max(2048,
            (int)(WaveFormat.SampleRate * WaveFormat.Channels * 0.05));
        var temp = new float[producerChunk];

        while (!ct.IsCancellationRequested)
        {
            int free;
            lock (bufLock)
            {
                if (disposed) return;
                free = ringCapacity - available;
            }
            // If the ring is mostly full, idle briefly. Avoids spinning when
            // the consumer is keeping pace; producer wakes ~every 10ms to
            // re-check.
            if (free < producerChunk / 2)
            {
                try { Thread.Sleep(10); } catch { return; }
                continue;
            }

            int toRead = Math.Min(producerChunk, free);
            int n;
            try
            {
                // This is the call that may block on cold I/O (ffmpeg
                // stdout, HTTP socket). Because we're on a background
                // thread, the audio thread doesn't care.
                n = inner.Read(temp, 0, toRead);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Prebuffer producer read error: {ex.Message}");
                lock (bufLock) eofReached = true;
                return;
            }

            if (n == 0)
            {
                lock (bufLock) eofReached = true;
                return;
            }

            lock (bufLock)
            {
                if (disposed) return;
                // Two-phase write to handle ring wrap-around.
                var first = Math.Min(n, ringCapacity - writePos);
                Array.Copy(temp, 0, ring, writePos, first);
                if (n > first)
                {
                    Array.Copy(temp, first, ring, 0, n - first);
                }
                writePos = (writePos + n) % ringCapacity;
                available += n;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (disposed) return 0;

        lock (bufLock)
        {
            if (available == 0)
            {
                // Producer reached EOF: propagate so the consumer (which is
                // typically StreamVoice → MixingSampleProvider) drops us.
                if (eofReached) return 0;
                // Underrun: the producer is still pumping but hasn't caught
                // up yet. Return silence at full count so the mixer treats us
                // as a still-live input — same shape StreamVoice uses for
                // inter-track gaps.
                Array.Clear(buffer, offset, count);
                return count;
            }

            var toRead = Math.Min(count, available);
            // Two-phase read to handle ring wrap-around.
            var first = Math.Min(toRead, ringCapacity - readPos);
            Array.Copy(ring, readPos, buffer, offset, first);
            if (toRead > first)
            {
                Array.Copy(ring, 0, buffer, offset + first, toRead - first);
            }
            readPos = (readPos + toRead) % ringCapacity;
            available -= toRead;

            if (toRead == count) return count;

            // Partial — distinguish drain-then-EOF from transient underrun.
            // EOF: return the partial; the consumer's MixingSampleProvider-
            // style "less than count = ended" contract fires the natural-end
            // path. Otherwise: pad with silence so we stay alive.
            if (eofReached) return toRead;
            Array.Clear(buffer, offset + toRead, count - toRead);
            return count;
        }
    }

    public double PositionSeconds => innerSeekable?.PositionSeconds ?? 0;

    public void SeekToSeconds(double seconds)
    {
        if (innerSeekable == null) return;
        // Drop pre-seek samples first so the consumer doesn't replay ~200ms
        // of stale audio after the seek lands. The producer's next inner.Read
        // will pick up post-seek samples from the respawned ffmpeg.
        FlushBuffer();
        innerSeekable.SeekToSeconds(seconds);
    }

    public void SkipToNext()
    {
        if (innerSkippable == null) return;
        // Same flush rationale as SeekToSeconds — without this, the user
        // hears the tail of the previous track for the buffered duration
        // before the next track starts.
        FlushBuffer();
        innerSkippable.SkipToNext();
    }

    private void FlushBuffer()
    {
        lock (bufLock)
        {
            readPos = 0;
            writePos = 0;
            available = 0;
        }
    }

    public bool DidExitCleanly() => innerCleanExit?.DidExitCleanly() ?? false;

    public void Dispose()
    {
        lock (bufLock)
        {
            if (disposed) return;
            disposed = true;
        }
        try { producerCts.Cancel(); } catch { /* ignore */ }
        // Best-effort wait so we tear down the inner only after the producer
        // has stopped touching it. The producer may still be inside a blocking
        // inner.Read; the wait timeout ensures Dispose can't hang on a wedged
        // network — the subsequent innerDisposable.Dispose will kill the
        // process / close the socket, which unblocks the producer.
        try { producerTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        try { innerDisposable.Dispose(); } catch { /* ignore */ }
        try { producerCts.Dispose(); } catch { /* ignore */ }
    }
}
