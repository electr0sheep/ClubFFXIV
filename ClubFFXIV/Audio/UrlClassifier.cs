using System;
using System.Text.RegularExpressions;

namespace ClubFFXIV.Audio;

/// <summary>
/// Decides which audio source backend to use for a given URL.
/// Direct HTTP/MP3/Icecast streams go to HttpAudioReader (fast, no subprocess).
/// Anything that needs URL extraction or HLS handling (Twitch, YouTube Live,
/// SoundCloud, etc.) goes through SubprocessAudioReader (yt-dlp + ffmpeg).
/// </summary>
public static class UrlClassifier
{
    // Hosts where yt-dlp is the right tool — anything that doesn't serve raw
    // MP3/OGG over HTTP. Conservative whitelist; we don't want to pay the
    // subprocess startup cost for direct streams.
    private static readonly Regex YtDlpHostsPattern = new(
        @"^https?://([^/]+\.)?(twitch\.tv|youtube\.com|youtu\.be|soundcloud\.com|mixcloud\.com|nicovideo\.jp|twitcasting\.tv)(/|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static AudioSourceKind ClassifyUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return AudioSourceKind.DirectHttp;
        return YtDlpHostsPattern.IsMatch(url)
            ? AudioSourceKind.YtDlp
            : AudioSourceKind.DirectHttp;
    }
}

public enum AudioSourceKind
{
    /// <summary>Plain HTTP audio stream (Icecast, Shoutcast, MP3 file URL).</summary>
    DirectHttp,
    /// <summary>Needs yt-dlp + ffmpeg to extract and decode.</summary>
    YtDlp,
}
