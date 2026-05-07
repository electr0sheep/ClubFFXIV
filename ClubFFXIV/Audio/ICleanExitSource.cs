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
