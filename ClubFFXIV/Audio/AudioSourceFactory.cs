using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Builds the raw audio source for a URL. Encapsulates the URL-classifier
/// branch so both <see cref="StreamPlayer"/> and <see cref="MultiStreamPlayer"/>
/// don't redo the same yt-dlp-vs-direct-HTTP plumbing. Caller is responsible
/// for checking <see cref="BinaryManager.Ready"/> first when the URL needs
/// external binaries — see <see cref="DescribeMissingBinaries"/>.
/// </summary>
internal static class AudioSourceFactory
{
    public static async Task<(ISampleProvider source, IDisposable disposable)> CreateAsync(
        string url,
        BinaryManager binaryManager,
        bool playlistRandom,
        string ytDlpCookiesBrowser,
        CancellationToken ct)
    {
        if (UrlClassifier.ClassifyUrl(url) == AudioSourceKind.YtDlp)
        {
            // PlaylistAudioReader holds the long-lived yt-dlp + iterates ffmpeg
            // invocations as items end. Single-video URLs degenerate to a
            // 1-item playlist with a clean EOF.
            var sub = await PlaylistAudioReader.CreateAsync(
                url, binaryManager, playlistRandom, ytDlpCookiesBrowser, ct).ConfigureAwait(false);
            return (sub, sub);
        }
        var http = await HttpAudioReader.CreateAsync(url, ct).ConfigureAwait(false);
        return (http, http);
    }

    /// <summary>
    /// Returns a human-readable description of any binaries needed for
    /// <paramref name="url"/> that aren't installed, or null when the URL
    /// needs no binaries or all are present. Centralised so the message
    /// stays consistent across the throw-on-missing (manual play) and
    /// log-on-missing (proximity tick) paths.
    /// </summary>
    public static string? DescribeMissingBinaries(string url, BinaryManager binaryManager)
    {
        if (UrlClassifier.ClassifyUrl(url) != AudioSourceKind.YtDlp) return null;
        if (binaryManager.Ready) return null;
        return !binaryManager.YtDlpInstalled && !binaryManager.FfmpegInstalled
            ? "yt-dlp + ffmpeg"
            : !binaryManager.YtDlpInstalled ? "yt-dlp" : "ffmpeg";
    }
}
