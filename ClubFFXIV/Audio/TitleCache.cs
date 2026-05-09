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
/// Per-URL artwork URL cache, populated by <see cref="YtDlpDisplayTitle"/>
/// from yt-dlp's <c>%(thumbnail)s</c> field. Icecast / direct-HTTP streams
/// don't expose artwork, so most non-yt-dlp URLs simply have no entry —
/// callers treat absence as "no thumbnail" and render a placeholder. The
/// stored value is a remote URL (http/https); the actual texture is fetched
/// and cached at render time by <c>UI.NowPlayingThumbnails</c>.
/// </summary>
internal static class ThumbnailCache
{
    private static readonly ConcurrentDictionary<string, string> map = new();

    public static void Set(string url, string thumbUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var trimmed = thumbUrl?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        // yt-dlp emits "NA" for missing fields when the alternation default
        // doesn't match; defensively skip these so we don't try to fetch
        // http://NA/ at render time.
        if (string.Equals(trimmed, "NA", StringComparison.Ordinal)) return;
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        map[url] = trimmed;
    }

    public static string? Get(string url) =>
        url != null && map.TryGetValue(url, out var t) ? t : null;
}

/// <summary>
/// Builds the yt-dlp <c>--print</c> template that replaces <c>-g</c> in our
/// URL-resolution invocations, and parses each emitted line into
/// (resolvedUrl, displayLabel, thumbnailUrl). One line per playlist item —
/// the URL goes to ffmpeg, the label goes to <see cref="TitleCache"/> and
/// the thumbnail URL goes to <see cref="ThumbnailCache"/>, both keyed by
/// the user's original URL.
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
    // %(field,alt|)s uses alternation with an empty default so missing
    // fields become "" instead of the literal "NA".  (ASCII unit
    // separator) splits URL from metadata — chosen because it can't appear
    // in user-facing text.
    private const char Sep = '';
    public const string PrintTemplate =
        "%(url)sARTIST:%(artist,creator|)sALBUM:%(album|)s" +
        "TRACK:%(track|)sTITLE:%(title|)s" +
        "UPLOADER:%(uploader,channel|)sEXT:%(extractor_key|)s" +
        "THUMB:%(thumbnail|)s";

    /// <summary>
    /// Parses a yt-dlp line, and if a display label is derivable, stores it
    /// in <see cref="TitleCache"/> under <paramref name="userUrlForCache"/>.
    /// Likewise, if a thumbnail URL is present it's stored in
    /// <see cref="ThumbnailCache"/>. Returns the resolved media URL, or null
    /// if the line had none.
    /// </summary>
    public static string? ParseAndCache(string line, string userUrlForCache)
    {
        var (url, label, thumb) = Parse(line.Trim());
        if (string.IsNullOrEmpty(url)) return null;
        if (!string.IsNullOrEmpty(label)) TitleCache.Set(userUrlForCache, label);
        if (!string.IsNullOrEmpty(thumb)) ThumbnailCache.Set(userUrlForCache, thumb!);
        return url;
    }

    public static (string Url, string? Label, string? Thumbnail) Parse(string line)
    {
        var parts = line.Split(Sep);
        if (parts.Length == 0) return ("", null, null);
        var url = parts[0].Trim();

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < parts.Length; i++)
        {
            var p = parts[i];
            var colon = p.IndexOf(':');
            if (colon <= 0) continue;
            fields[p[..colon]] = p[(colon + 1)..].Trim();
        }
        var thumb = Get(fields, "THUMB");
        return (url, BuildLabel(fields), thumb.Length > 0 ? thumb : null);
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
