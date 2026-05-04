using System;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Plays an HTTP audio stream (Icecast/Shoutcast/MP3 over HTTP) via Media Foundation.
/// MediaFoundationReader handles MP3 and AAC streams natively on Windows.
/// OGG/Vorbis Icecast streams need a different reader (NVorbis) — out of scope for Phase 1.
/// </summary>
public sealed class StreamPlayer : IDisposable
{
    private MediaFoundationReader? reader;
    private WaveOutEvent? output;
    private float volume = 0.7f;
    private string? currentUrl;

    public bool IsPlaying => output?.PlaybackState == PlaybackState.Playing;
    public string? CurrentUrl => currentUrl;

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0f, 1f);
            if (output != null) output.Volume = volume;
        }
    }

    public void Play(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is empty", nameof(url));

        Stop();

        // Constructor blocks until enough has buffered to determine format.
        // For a remote stream this is typically 1–3s. Caller should handle UI feedback.
        reader = new MediaFoundationReader(url);
        output = new WaveOutEvent();
        output.Init(reader);
        output.Volume = volume;
        output.Play();
        currentUrl = url;
    }

    public void Stop()
    {
        try { output?.Stop(); } catch { /* swallow — disposing anyway */ }
        output?.Dispose();
        reader?.Dispose();
        output = null;
        reader = null;
        currentUrl = null;
    }

    public void Dispose() => Stop();
}
