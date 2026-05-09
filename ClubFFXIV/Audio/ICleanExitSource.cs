namespace ClubFFXIV.Audio;

/// <summary>
/// Audio source that can report whether it reached EOF on its own (clean
/// finish) vs. was killed (user stop / replacement). <see cref="StreamPlayer"/>
/// uses this to decide whether <c>NAudio</c>'s PlaybackStopped event is a
/// natural end (eligible for auto-loop) or a user-driven teardown (skip).
/// Implemented by <see cref="SubprocessAudioReader"/> (single ffmpeg) and
/// <see cref="PlaylistAudioReader"/> (yt-dlp playlist iteration).
/// </summary>
public interface ICleanExitSource
{
    bool DidExitCleanly();
}

/// <summary>
/// Audio source that exposes a seekable playhead — currently only the
/// ffmpeg-backed paths (<see cref="SubprocessAudioReader"/> and, by
/// delegation to the current item, <see cref="PlaylistAudioReader"/>).
/// Direct HTTP MP3 streams don't implement this in v1: Icecast is live
/// (no meaningful seek) and seeking a static MP3 file would need HTTP
/// Range support — out of scope for the first cut. The Now Playing UI
/// gates the seek bar on this interface AND <c>!IsLive</c> together, so
/// "seekable but live" never renders transport.
/// </summary>
public interface ISeekableSource
{
    /// <summary>Elapsed audio seconds since playback (or last seek).</summary>
    double PositionSeconds { get; }

    /// <summary>
    /// Move the playhead to <paramref name="seconds"/>. Implementations may
    /// emit a brief gap of silence while the underlying decoder restarts;
    /// the call returns immediately and is safe to invoke from any thread.
    /// </summary>
    void SeekToSeconds(double seconds);
}

/// <summary>
/// Audio source that can advance to the next item in a multi-item stream
/// — currently only <see cref="PlaylistAudioReader"/>. The implementation
/// piggybacks on the existing inter-item EOF flow (dispose the inner
/// ffmpeg, the wrapper reads the next URL from yt-dlp's stdout). Single-
/// video URLs go through the same playlist wrapper but degenerate to one
/// item; calling SkipToNext on those exhausts the playlist early, which
/// the host's natural-end logic handles (loop-on → restart, loop-off →
/// stop). Safe to call from any thread.
/// </summary>
public interface ISkippableSource
{
    void SkipToNext();
}
