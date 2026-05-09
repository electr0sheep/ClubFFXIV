using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ClubFFXIV.Audio;

/// <summary>
/// Per-URL display title cache, populated by audio readers as part of their
/// CreateAsync (yt-dlp's --print template for Yt/Twitch/SoundCloud, icy-name
/// + inline StreamTitle metadata for direct HTTP/Icecast). The Now Playing
/// header reads it via Plugin.GetUrlTitle to render a friendlier label than
/// the raw URL. Lives for the plugin instance lifetime — a YouTube video
/// looping keeps its title without a re-fetch, and Icecast track changes
/// (and yt-dlp playlist advances) overwrite the same key as new tracks
/// arrive.
/// </summary>
internal static class TitleCache
{
    private static readonly ConcurrentDictionary<string, string> map = new();

    public static void Set(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        map[url] = trimmed;
    }

    public static string? Get(string url) =>
        url != null && map.TryGetValue(url, out var t) ? t : null;
}

/// <summary>
/// Builds the yt-dlp <c>--print</c> template that replaces <c>-g</c> in our
/// URL-resolution invocations, and parses each emitted line into
/// (resolvedUrl, displayLabel). One line per playlist item — the URL goes to
/// ffmpeg, the label goes to <see cref="TitleCache"/> keyed by the user's
/// original URL.
///
/// Display rules per source:
///   • YouTube Music / "Topic" channels (artist + track populated):
///       artist - album - track   (album omitted if missing)
///   • Twitch streams (extractor key starts with "Twitch"):
///       channel - stream title
///   • Regular YouTube / SoundCloud / fallback: title alone
///   • Nothing usable: null → caller leaves cache untouched, header falls
///     back to the truncated URL.
/// </summary>
internal static class YtDlpDisplayTitle
{
    // yt-dlp output template. <c>%(url)s</c> is the post-format-selection
    // media URL (same value -g would print). Each metadata field uses
    // <c>field,alt|</c> alternation with empty default so missing values
    // become empty strings rather than the literal "NA".
    //
    // <c>|@|</c> is an unlikely-to-appear separator; if a song title
    // genuinely contains it the parse is malformed and we just skip the
    // label (URL still resolves), which is acceptable degradation.
    public const string PrintTemplate =
        "%(url)s|@|ARTIST:%(artist,creator|)s|@|ALBUM:%(album|)s" +
        "|@|TRACK:%(track|)s|@|TITLE:%(title|)s" +
        "|@|UPLOADER:%(uploader,channel|)s|@|EXT:%(extractor_key|)s";

    public static (string Url, string? Label) Parse(string line)
    {
        var parts = line.Split("|@|");
        if (parts.Length == 0) return ("", null);
        var url = parts[0].Trim();

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < parts.Length; i++)
        {
            var p = parts[i];
            var colon = p.IndexOf(':');
            if (colon <= 0) continue;
            fields[p[..colon]] = p[(colon + 1)..].Trim();
        }
        return (url, BuildLabel(fields));
    }

    private static string? BuildLabel(Dictionary<string, string> f)
    {
        var artist = Get(f, "ARTIST");
        var album = Get(f, "ALBUM");
        var track = Get(f, "TRACK");
        var title = Get(f, "TITLE");
        var uploader = Get(f, "UPLOADER");
        var ext = Get(f, "EXT");

        if (artist.Length > 0 && track.Length > 0)
            return album.Length > 0
                ? $"{artist} - {album} - {track}"
                : $"{artist} - {track}";

        if (ext.StartsWith("Twitch", StringComparison.OrdinalIgnoreCase)
            && uploader.Length > 0 && title.Length > 0)
            return $"{uploader} - {title}";

        if (title.Length > 0) return title;
        if (uploader.Length > 0) return uploader;
        return null;
    }

    private static string Get(Dictionary<string, string> f, string key) =>
        f.TryGetValue(key, out var v) ? v : "";
}
