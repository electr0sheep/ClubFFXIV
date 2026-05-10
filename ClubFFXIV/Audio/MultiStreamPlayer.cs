using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClubFFXIV.Audio;

/// <summary>
/// Multi-voice variant of <see cref="StreamPlayer"/>. Owns a single
/// MixingSampleProvider feeding a single WaveOutEvent. Each voice represents
/// one club's stream; voices are keyed by canonical plot key so the outdoor
/// proximity diff loop can add/remove them by identity.
///
/// Per-voice resamplers + multiple concurrent decoders (especially yt-dlp
/// subprocesses) compound CPU/network/memory linearly, so the default
/// concurrency cap stays low — see <see cref="Configuration.MaxConcurrentStreams"/>.
/// </summary>
internal sealed class MultiStreamPlayer : IDisposable
{
    public const float BypassCutoffHz = 20000f;

    // Per-voice prebuffer to absorb cold-start I/O latency (ffmpeg pipe
    // priming, HTTP connect, HLS first segment fetch) on a background
    // producer thread instead of the audio thread. Without this, the first
    // mixer Read on a newly-added voice blocks on its source's stdout — and
    // because MixingSampleProvider walks inputs sequentially, that stalls
    // every already-playing voice and audibly stutters.
    //
    // Buffer is sized to comfortably cover one WaveOutEvent buffer fill
    // (default DesiredLatency / 2 ≈ 150ms) with margin. Prime target is
    // ~70% of the buffer so the producer has headroom; the prime timeout
    // bounds AddVoiceAsync so a wedged stream can't hold the proximity
    // tick's pendingStarts slot forever.
    private const double PrebufferSeconds = 0.5;
    private const double PrimeTargetSeconds = 0.35;
    private static readonly TimeSpan PrimeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Canonical mixer format. SubprocessAudioReader already emits this exact
    /// shape, so yt-dlp voices skip the resampler. HttpAudioReader sources
    /// vary by source file (22.05/32/44.1/48 kHz, mono or stereo) and get
    /// upmixed + resampled to match.
    /// </summary>
    private static readonly WaveFormat MixerFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly BinaryManager binaryManager;
    private readonly object voicesLock = new();
    private readonly Dictionary<string, StreamVoice> voices = new();
    private readonly Dictionary<string, CancellationTokenSource> pendingStarts = new();

    private readonly MixingSampleProvider mixer;
    private readonly WaveOutEvent output;

    private float masterVolume = 0.7f;
    // Set by the framework tick on focus-loss; ORed with per-voice mute.
    private bool autoMuted;
    // Per-voice user mutes from the Now Playing header. Lives behind
    // voicesLock so SetSpatial reads consistently.
    private readonly HashSet<string> mutedKeys = new();

    /// <summary>
    /// Forwarded to yt-dlp as --playlist-random when adding a new voice.
    /// Pushed by <see cref="Plugin.SyncYtDlpOptions"/> when the user toggles it.
    /// </summary>
    public bool PlaylistRandom { get; set; }

    /// <summary>
    /// Browser name passed to yt-dlp's --cookies-from-browser, or empty to
    /// disable. Pushed by <see cref="Plugin.SyncYtDlpOptions"/> when the user
    /// edits it; read at each <see cref="AddVoiceAsync"/> call.
    /// </summary>
    public string YtDlpCookiesBrowser { get; set; } = "";

    /// <summary>
    /// Fires when an individual voice's source reached a natural EOF
    /// (eligible for auto-loop). Args: (canonicalKey, url). Plugin's
    /// loop handler decides whether to <see cref="AddVoiceAsync"/> the same
    /// URL back, gated on the user's LoopFinishedVideos preference.
    /// Voices that are removed via <see cref="RemoveVoice"/> or
    /// <see cref="StopAll"/> do NOT fire this — the mixer-input-ended
    /// handler distinguishes natural EOF from teardown via
    /// <see cref="StreamVoice.CleanExit"/>.
    /// </summary>
    public event Action<string, string>? VoiceNaturallyEnded;

    public MultiStreamPlayer(BinaryManager binaryManager)
    {
        this.binaryManager = binaryManager;
        // ReadFully = mixer pads with silence when no inputs are present, so
        // the WaveOutEvent never thinks the stream ended just because all
        // voices were temporarily removed.
        mixer = new MixingSampleProvider(MixerFormat) { ReadFully = true };
        // NAudio fires MixerInputEnded when an input returns less than the
        // requested sample count — the chain's natural EOF signal. Use this
        // to drop the voice from our dict (mixer already removes it from its
        // inputs) and surface the loop-eligible event.
        mixer.MixerInputEnded += OnMixerInputEnded;
        output = new WaveOutEvent();
        output.Init(mixer.ToWaveProvider());
        output.Play();
    }

    // Fires synchronously on the audio thread (inside MixingSampleProvider.Read)
    // when an input returns less than count — i.e. the voice's source EOF'd.
    // Hand identification + dict bookkeeping happen here under the lock; the
    // Dispose + event fanout get posted to the threadpool because they may
    // do slow work (process kills, yt-dlp respawn) that would stall mixer reads.
    // Note: per-voice mute state in mutedKeys is intentionally preserved here
    // so a user-muted voice stays muted across natural-end loop iterations.
    // The walk-out-of-range path (RemoveVoice) clears mutedKeys, which is
    // the right place — that's a "fresh sound" transition, not a loop.
    private void OnMixerInputEnded(object? sender, SampleProviderEventArgs e)
    {
        if (e.SampleProvider is not StreamVoice voice) return;

        bool weOwnIt;
        bool naturalEnd;
        lock (voicesLock)
        {
            // Identify by reference, not key — a teardown + re-add for the same
            // key could race the mixer's drain and we'd otherwise misattribute
            // the event to the new voice.
            if (voices.TryGetValue(voice.CanonicalKey, out var stored) && ReferenceEquals(stored, voice))
            {
                voices.Remove(voice.CanonicalKey);
                weOwnIt = true;
            }
            else
            {
                weOwnIt = false;
            }
            naturalEnd = voice.CleanExit?.DidExitCleanly() == true;
        }

        Plugin.Log.Info(
            $"[MS-DIAG] MixerInputEnded key={voice.CanonicalKey} url={voice.Url} weOwnIt={weOwnIt} naturalEnd={naturalEnd} cleanExitField={(voice.CleanExit != null)}");

        if (!weOwnIt) return;

        var key = voice.CanonicalKey;
        var url = voice.Url;
        _ = Task.Run(() =>
        {
            if (naturalEnd) VoiceNaturallyEnded?.Invoke(key, url);
            try { voice.Dispose(); } catch { /* swallow during teardown */ }
        });
    }

    public int VoiceCount
    {
        get { lock (voicesLock) return voices.Count; }
    }

    public bool HasVoice(string canonicalKey)
    {
        lock (voicesLock) return voices.ContainsKey(canonicalKey);
    }

    public bool IsStarting(string canonicalKey)
    {
        lock (voicesLock) return pendingStarts.ContainsKey(canonicalKey);
    }

    /// <summary>
    /// True if any voice is playing or mid-startup. Used by the BGM-muter
    /// policy: an indoor multi-stream voice still buffering should mute the
    /// game's own music, just like a single-stream pendingStartUrl does.
    /// </summary>
    public bool HasAnyActivity
    {
        get { lock (voicesLock) return voices.Count > 0 || pendingStarts.Count > 0; }
    }

    /// <summary>Snapshot of currently-playing canonical keys.</summary>
    public List<string> ActiveKeys()
    {
        lock (voicesLock) return new List<string>(voices.Keys);
    }

    public float MasterVolume
    {
        get => masterVolume;
        set => masterVolume = Math.Clamp(value, 0f, 1f);
        // Per-voice volume is reapplied each frame by SetSpatial; no need to
        // walk the voice table on master change. UI slider feels instant
        // because the proximity loop ticks every frame.
    }

    /// <summary>Global auto-mute (focus loss). Per-voice mute lives separately.</summary>
    public bool AutoMuted
    {
        get => autoMuted;
        set
        {
            if (autoMuted == value) return;
            autoMuted = value;
            // Fast-path on mute: zero out every voice immediately so the
            // user doesn't hear one-frame leakage. Unmute relies on the next
            // SetSpatial tick to restore each voice's actual volume.
            if (autoMuted)
            {
                lock (voicesLock)
                {
                    foreach (var v in voices.Values) v.Volume = 0f;
                }
            }
        }
    }

    /// <summary>True if the named voice is currently user-muted.</summary>
    public bool IsVoiceMuted(string canonicalKey)
    {
        lock (voicesLock) return mutedKeys.Contains(canonicalKey);
    }

    /// <summary>
    /// Toggle (or set) per-voice user mute. Effective immediately — when set
    /// to muted, voice volume drops to 0; the next SetSpatial tick won't
    /// restore it until the user unmutes. Composes with <see cref="AutoMuted"/>.
    /// </summary>
    public void SetVoiceMuted(string canonicalKey, bool muted)
    {
        lock (voicesLock)
        {
            var changed = muted ? mutedKeys.Add(canonicalKey) : mutedKeys.Remove(canonicalKey);
            if (!changed) return;
            if (muted && voices.TryGetValue(canonicalKey, out var v))
                v.Volume = 0f;
        }
    }

    /// <summary>Stream URL for an active voice, or null if the key isn't mixed.</summary>
    public string? GetVoiceUrl(string canonicalKey)
    {
        lock (voicesLock)
            return voices.TryGetValue(canonicalKey, out var v) ? v.Url : null;
    }

    /// <summary>
    /// True iff the named voice exists and is currently paused. Returns false
    /// for unknown keys so the UI can call without first checking HasVoice.
    /// </summary>
    public bool IsVoicePaused(string canonicalKey)
    {
        lock (voicesLock)
            return voices.TryGetValue(canonicalKey, out var v) && v.Paused;
    }

    /// <summary>
    /// True iff the named voice exists and its source is seekable. Read by
    /// the Now Playing UI to decide whether to render the seek bar — combined
    /// with the cached IsLive flag. No-op for unknown keys.
    /// </summary>
    public bool IsVoiceSeekable(string canonicalKey)
    {
        lock (voicesLock)
            return voices.TryGetValue(canonicalKey, out var v) && v.IsSeekable;
    }

    /// <summary>Elapsed seconds in the named voice, or 0 if unknown / unseekable.</summary>
    public double GetVoicePosition(string canonicalKey)
    {
        lock (voicesLock)
            return voices.TryGetValue(canonicalKey, out var v) ? v.PositionSeconds : 0;
    }

    /// <summary>
    /// Set per-voice pause state. While paused the voice emits silence into
    /// the mix without draining its source — ffmpeg back-pressures, network
    /// pulls stop. Holds across natural-end events so a user-paused voice
    /// stays paused; cleared on RemoveVoice (walking out of range).
    /// </summary>
    public void SetVoicePaused(string canonicalKey, bool paused)
    {
        lock (voicesLock)
        {
            if (voices.TryGetValue(canonicalKey, out var v))
                v.Paused = paused;
        }
    }

    /// <summary>
    /// Move the named voice's playhead. No-op for unknown keys or
    /// non-seekable sources, so the UI doesn't need to pre-check. Safe to
    /// call from any thread — the underlying source handles the audio-thread
    /// coordination.
    /// </summary>
    public void SeekVoice(string canonicalKey, double seconds)
    {
        StreamVoice? v;
        lock (voicesLock)
        {
            voices.TryGetValue(canonicalKey, out v);
        }
        // SeekToSeconds is safe outside the lock — it just forwards to the
        // source's own thread-safe entry point. Holding voicesLock through
        // a kill+respawn would block proximity ticks.
        v?.SeekToSeconds(seconds);
    }

    /// <summary>
    /// True iff the named voice exists and its source supports skip-next
    /// (i.e. it's a yt-dlp playlist wrapper). UI gate for the skip icon.
    /// </summary>
    public bool IsVoiceSkippable(string canonicalKey)
    {
        lock (voicesLock)
            return voices.TryGetValue(canonicalKey, out var v) && v.IsSkippable;
    }

    /// <summary>
    /// Advance the named voice to its source's next playlist item. No-op
    /// for unknown keys or non-skippable sources. Released outside the
    /// lock for the same reason as SeekVoice.
    /// </summary>
    public void SkipVoiceToNext(string canonicalKey)
    {
        StreamVoice? v;
        lock (voicesLock)
        {
            voices.TryGetValue(canonicalKey, out v);
        }
        v?.SkipToNext();
    }

    /// <summary>
    /// Async because source construction blocks on network buffering / yt-dlp
    /// subprocess startup. Returns false if the voice was rejected (already
    /// active / starting, missing binaries, cancelled, or source error).
    /// </summary>
    public async Task<bool> AddVoiceAsync(
        string canonicalKey,
        string url,
        float initialCutoffHz,
        CancellationToken externalCt = default)
    {
        CancellationTokenSource cts;
        lock (voicesLock)
        {
            if (voices.ContainsKey(canonicalKey))
            {
                Plugin.Log.Info($"[MS-DIAG] AddVoiceAsync REJECT-already-active key={canonicalKey} url={url}");
                return false;
            }
            if (pendingStarts.ContainsKey(canonicalKey))
            {
                Plugin.Log.Info($"[MS-DIAG] AddVoiceAsync REJECT-already-starting key={canonicalKey} url={url}");
                return false;
            }
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            pendingStarts[canonicalKey] = cts;
        }
        Plugin.Log.Info($"[MS-DIAG] AddVoiceAsync START key={canonicalKey} url={url} cutoff={initialCutoffHz}");

        // toDispose tracks the *current* owner of cleanup responsibility as
        // ownership migrates: raw source → prebuffer wrapper → StreamVoice.
        // Set to null once the voice has taken ownership.
        IDisposable? toDispose = null;
        try
        {
            if (AudioSourceFactory.DescribeMissingBinaries(url, binaryManager) != null)
            {
                Plugin.Log.Warning(
                    $"MultiStreamPlayer: skipping {url} — yt-dlp/ffmpeg not installed.");
                return false;
            }
            var (rawSource, rawDisposable) = await AudioSourceFactory.CreateAsync(
                url, binaryManager, PlaylistRandom, YtDlpCookiesBrowser, cts.Token).ConfigureAwait(false);
            toDispose = rawDisposable;

            cts.Token.ThrowIfCancellationRequested();

            // Wrap with the prebuffer so the audio thread's mixer.Read never
            // blocks on cold I/O. The wrapper spawns a background producer
            // task immediately and takes ownership of the raw source's
            // disposable.
            var buffered = new PrebufferedSampleProvider(rawSource, toDispose, PrebufferSeconds);
            toDispose = buffered;

            // Prime synchronously before adding to the mixer. If the inner's
            // first read takes longer than PrimeTimeout (wedged network,
            // ffmpeg unable to connect), proceed anyway — the wrapper returns
            // silence on underrun, so the voice goes live silent and audio
            // appears as soon as the producer catches up. Guarantees the
            // proximity tick's pendingStarts slot can't be held hostage by a
            // dead URL.
            using (var primeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                primeCts.CancelAfter(PrimeTimeout);
                try
                {
                    await buffered.PrimeAsync(PrimeTargetSeconds, primeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
                {
                    // Prime timeout — fall through and let the wrapper play
                    // silence until the producer eventually catches up.
                    Plugin.Log.Info(
                        $"MultiStreamPlayer: prime timeout for {url}; adding voice anyway.");
                }
            }

            cts.Token.ThrowIfCancellationRequested();

            var voice = new StreamVoice(
                canonicalKey, url, buffered, buffered, MixerFormat, initialCutoffHz,
                overrideSeekable: buffered.SupportsSeek,
                overrideSkippable: buffered.SupportsSkip,
                overrideCleanExit: buffered.SupportsCleanExit);
            toDispose = null; // ownership transferred to voice

            lock (voicesLock)
            {
                if (!pendingStarts.TryGetValue(canonicalKey, out var stillCts) || stillCts != cts)
                {
                    // Removed or superseded mid-flight by RemoveVoice. Drop it.
                    Plugin.Log.Info($"[MS-DIAG] AddVoiceAsync ABORT-superseded key={canonicalKey} url={url}");
                    voice.Dispose();
                    return false;
                }
                pendingStarts.Remove(canonicalKey);
                voices[canonicalKey] = voice;
                mixer.AddMixerInput(voice);
            }
            Plugin.Log.Info($"[MS-DIAG] AddVoiceAsync ADDED key={canonicalKey} url={url}");
            return true;
        }
        catch (OperationCanceledException)
        {
            Plugin.Log.Info($"[MS-DIAG] AddVoiceAsync CANCELLED key={canonicalKey} url={url}");
            toDispose?.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"MultiStreamPlayer: voice startup failed for {url}: {ex.Message}");
            toDispose?.Dispose();
            return false;
        }
        finally
        {
            lock (voicesLock)
            {
                if (pendingStarts.TryGetValue(canonicalKey, out var stillCts) && stillCts == cts)
                    pendingStarts.Remove(canonicalKey);
            }
            cts.Dispose();
        }
    }

    public void RemoveVoice(string canonicalKey)
    {
        StreamVoice? toDispose = null;
        CancellationTokenSource? toCancel = null;
        bool hadPending;
        bool hadVoice;
        lock (voicesLock)
        {
            if (pendingStarts.TryGetValue(canonicalKey, out var pendingCts))
            {
                toCancel = pendingCts;
                pendingStarts.Remove(canonicalKey);
                hadPending = true;
            }
            else hadPending = false;
            if (voices.TryGetValue(canonicalKey, out var voice))
            {
                mixer.RemoveMixerInput(voice);
                voices.Remove(canonicalKey);
                toDispose = voice;
                hadVoice = true;
            }
            else hadVoice = false;
            // Don't carry per-voice mute state across remove/re-add: when the
            // voice next pops up via proximity, treat it as a fresh sound.
            // Per-voice pause state lives on the StreamVoice itself, which
            // gets disposed below — so pause is also implicitly reset on
            // re-entry, which matches the "fresh sound" intent. (We can't
            // preserve a paused playhead anyway — yt-dlp restarts from the
            // top of the playlist.)
            mutedKeys.Remove(canonicalKey);
        }
        if (hadPending || hadVoice)
        {
            var caller = new System.Diagnostics.StackFrame(1, false).GetMethod()?.Name ?? "?";
            Plugin.Log.Info(
                $"[MS-DIAG] RemoveVoice key={canonicalKey} hadPending={hadPending} hadVoice={hadVoice} caller={caller}");
        }
        try { toCancel?.Cancel(); } catch { /* ignore */ }
        toDispose?.Dispose();
    }

    /// <summary>
    /// Apply per-voice spatial parameters from the proximity result. No-op if
    /// the voice isn't active (e.g. still building or already removed).
    /// <paramref name="pan"/> is -1..+1 (0 = centered); pass 0 when directional
    /// audio is disabled.
    /// </summary>
    public void SetSpatial(string canonicalKey, float spatialVolume, float cutoffHz, float pan = 0f)
    {
        lock (voicesLock)
        {
            if (!voices.TryGetValue(canonicalKey, out var voice)) return;
            var muted = autoMuted || mutedKeys.Contains(canonicalKey);
            voice.Volume = muted ? 0f : (masterVolume * spatialVolume);
            voice.CutoffHz = cutoffHz;
            voice.Pan = pan;
        }
    }

    public void StopAll()
    {
        StreamVoice[] toDispose;
        CancellationTokenSource[] toCancel;
        lock (voicesLock)
        {
            toDispose = new StreamVoice[voices.Count];
            voices.Values.CopyTo(toDispose, 0);
            foreach (var v in toDispose) mixer.RemoveMixerInput(v);
            voices.Clear();

            toCancel = new CancellationTokenSource[pendingStarts.Count];
            pendingStarts.Values.CopyTo(toCancel, 0);
            pendingStarts.Clear();
        }
        foreach (var cts in toCancel) { try { cts.Cancel(); } catch { /* ignore */ } }
        foreach (var v in toDispose) v.Dispose();
    }

    public void Dispose()
    {
        StopAll();
        try { output.Stop(); } catch { /* swallow during teardown */ }
        output.Dispose();
    }
}
