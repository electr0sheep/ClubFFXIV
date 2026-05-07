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
    /// Plugin syncs this from <see cref="Configuration.PlaylistRandom"/>.
    /// </summary>
    public bool PlaylistRandom { get; set; }

    public MultiStreamPlayer(BinaryManager binaryManager)
    {
        this.binaryManager = binaryManager;
        // ReadFully = mixer pads with silence when no inputs are present, so
        // the WaveOutEvent never thinks the stream ended just because all
        // voices were temporarily removed.
        mixer = new MixingSampleProvider(MixerFormat) { ReadFully = true };
        output = new WaveOutEvent();
        output.Init(mixer.ToWaveProvider());
        output.Play();
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
            if (voices.ContainsKey(canonicalKey)) return false;
            if (pendingStarts.ContainsKey(canonicalKey)) return false;
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            pendingStarts[canonicalKey] = cts;
        }

        ISampleProvider? source = null;
        IDisposable? disposable = null;
        try
        {
            var kind = UrlClassifier.ClassifyUrl(url);
            if (kind == AudioSourceKind.YtDlp)
            {
                if (!binaryManager.Ready)
                {
                    Plugin.Log.Warning(
                        $"MultiStreamPlayer: skipping {url} — yt-dlp/ffmpeg not installed.");
                    return false;
                }
                var sub = await SubprocessAudioReader.CreateAsync(url, binaryManager, PlaylistRandom, cts.Token).ConfigureAwait(false);
                source = sub;
                disposable = sub;
            }
            else
            {
                var http = await HttpAudioReader.CreateAsync(url, cts.Token).ConfigureAwait(false);
                source = http;
                disposable = http;
            }

            cts.Token.ThrowIfCancellationRequested();

            var voice = new StreamVoice(canonicalKey, url, source, disposable, MixerFormat, initialCutoffHz);
            disposable = null; // ownership transferred to voice

            lock (voicesLock)
            {
                if (!pendingStarts.TryGetValue(canonicalKey, out var stillCts) || stillCts != cts)
                {
                    // Removed or superseded mid-flight by RemoveVoice. Drop it.
                    voice.Dispose();
                    return false;
                }
                pendingStarts.Remove(canonicalKey);
                voices[canonicalKey] = voice;
                mixer.AddMixerInput(voice);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            disposable?.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"MultiStreamPlayer: voice startup failed for {url}: {ex.Message}");
            disposable?.Dispose();
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
        lock (voicesLock)
        {
            if (pendingStarts.TryGetValue(canonicalKey, out var pendingCts))
            {
                toCancel = pendingCts;
                pendingStarts.Remove(canonicalKey);
            }
            if (voices.TryGetValue(canonicalKey, out var voice))
            {
                mixer.RemoveMixerInput(voice);
                voices.Remove(canonicalKey);
                toDispose = voice;
            }
            // Don't carry per-voice mute state across remove/re-add: when the
            // voice next pops up via proximity, treat it as a fresh sound.
            mutedKeys.Remove(canonicalKey);
        }
        try { toCancel?.Cancel(); } catch { /* ignore */ }
        toDispose?.Dispose();
    }

    /// <summary>
    /// Apply per-voice spatial parameters from the proximity result. No-op if
    /// the voice isn't active (e.g. still building or already removed).
    /// </summary>
    public void SetSpatial(string canonicalKey, float spatialVolume, float cutoffHz)
    {
        lock (voicesLock)
        {
            if (!voices.TryGetValue(canonicalKey, out var voice)) return;
            var muted = autoMuted || mutedKeys.Contains(canonicalKey);
            voice.Volume = muted ? 0f : (masterVolume * spatialVolume);
            voice.CutoffHz = cutoffHz;
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
