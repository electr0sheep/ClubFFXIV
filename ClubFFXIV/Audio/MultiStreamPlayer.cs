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
    private bool muted;

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

    public bool Muted
    {
        get => muted;
        set
        {
            if (muted == value) return;
            muted = value;
            // Fast-path on mute: zero out every voice immediately so the
            // user doesn't hear one-frame leakage. Unmute relies on the next
            // SetSpatial tick to restore each voice's actual volume.
            if (muted)
            {
                lock (voicesLock)
                {
                    foreach (var v in voices.Values) v.Volume = 0f;
                }
            }
        }
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
                var sub = await SubprocessAudioReader.CreateAsync(url, binaryManager, cts.Token).ConfigureAwait(false);
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
