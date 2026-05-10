using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClubFFXIV.Audio;

/// <summary>
/// Tiny shared helper for background-draining a child process's stderr.
/// Both <see cref="SubprocessAudioReader"/> (ffmpeg) and
/// <see cref="PlaylistAudioReader"/> (yt-dlp) need exactly this — drain
/// the pipe so the process doesn't stall on a full kernel buffer, and
/// surface stderr lines as <c>[tag] line</c> info logs.
/// </summary>
internal static class ProcessLog
{
    /// <summary>
    /// Spawn a background task that drains <paramref name="proc"/>'s stderr,
    /// logging each line at Info with <paramref name="tag"/> as the prefix.
    /// <paramref name="onDrainComplete"/> fires once after EOF / cancellation
    /// so callers can attribute unexpected exits or do other post-mortem work.
    /// </summary>
    public static Task DrainStderrInBackground(
        Process proc, string tag,
        CancellationToken ct = default,
        Action? onDrainComplete = null)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync(ct) is { } line)
                    Plugin.Log.Info($"[{tag}] {line}");
            }
            catch { /* process exited or token cancelled */ }
            try { onDrainComplete?.Invoke(); } catch { /* never let bookkeeping leak */ }
        }, ct);
    }
}
