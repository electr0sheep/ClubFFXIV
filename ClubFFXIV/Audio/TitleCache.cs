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
/// Cached thumbnail metadata for a stream URL. <c>Url</c> is the remote
/// artwork URL; <c>CropToSquare</c> tells the renderer to cover-crop the
/// centre square out of a non-square image. Set true for sources known to
/// pad square album art into a 16:9 frame (YouTube Music, YouTube Topic
/// channels) — those pad bars are wasted slot space, not part of the art.
/// </summary>
internal readonly record struct ThumbnailInfo(string Url, bool CropToSquare);

/// <summary>
/// Per-URL "is this a live stream?" flag. Set during source resolution —
/// from yt-dlp's <c>live_status</c> field (is_live / is_upcoming /
/// post_live → live; not_live / was_live / unset → non-live), or from
/// HTTP response headers (icy-metaint / icy-name → Icecast/Shoutcast,
/// always live). Read by the Now Playing UI to decide whether to render
/// transport controls (play/pause/seek) — live rows keep mute-only chrome
/// because pausing a live source has no useful semantics. Defaults to
/// true (treat as live) when nothing populated the cache, so an unknown
/// source doesn't get transport controls it can't honour.
/// </summary>
internal static class LiveStatusCache
{
    private static readonly ConcurrentDictionary<string, bool> map = new();

    public static void Set(string url, bool isLive)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        map[url] = isLive;
    }

    /// <summary>Returns the cached flag, or true (assume live) if unknown.</summary>
    public static bool IsLive(string url) =>
        url != null && map.TryGetValue(url, out var v) ? v : true;

    /// <summary>True iff a value has been explicitly cached for this URL.</summary>
    public static bool HasEntry(string url) =>
        url != null && map.ContainsKey(url);
}

/// <summary>
/// Per-URL duration in seconds for non-live sources, populated alongside
/// the live-status flag during source resolution. Zero / absent means
/// "unknown" — the UI shows the elapsed timestamp but no progress bar.
/// </summary>
internal static class DurationCache
{
    private static readonly ConcurrentDictionary<string, double> map = new();

    public static void Set(string url, double seconds)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!(seconds > 0) || double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
        map[url] = seconds;
    }

    public static double Get(string url) =>
        url != null && map.TryGetValue(url, out var v) ? v : 0;
}

/// <summary>
/// Per-URL artwork cache, populated by <see cref="YtDlpDisplayTitle"/> from
/// yt-dlp's <c>%(thumbnail)s</c> field. Icecast / direct-HTTP streams don't
/// expose artwork, so most non-yt-dlp URLs simply have no entry — callers
/// treat absence as "no thumbnail" and render a placeholder. The stored
/// value carries both the remote URL and a per-source render policy
/// (see <see cref="ThumbnailInfo"/>); the actual texture is fetched and
/// cached at render time by <c>UI.NowPlayingThumbnails</c>.
/// </summary>
internal static class ThumbnailCache
{
    private static readonly ConcurrentDictionary<string, ThumbnailInfo> map = new();

    public static void Set(string url, string thumbUrl, bool cropToSquare)
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
        map[url] = new ThumbnailInfo(trimmed, cropToSquare);
    }

    public static ThumbnailInfo? Get(string url) =>
        url != null && map.TryGetValue(url, out var info) ? info : null;
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
        "THUMB:%(thumbnail|)sLIVE:%(live_status|)s" +
        "DUR:%(duration|)s";

    /// <summary>
    /// Parses a yt-dlp line, and if a display label is derivable, stores it
    /// in <see cref="TitleCache"/> under <paramref name="userUrlForCache"/>.
    /// Likewise, if a thumbnail URL is present it's stored in
    /// <see cref="ThumbnailCache"/>. Live status and duration are also cached
    /// so the Now Playing UI can decide whether to draw transport controls.
    /// Returns the resolved media URL, or null if the line had none.
    /// </summary>
    public static string? ParseAndCache(string line, string userUrlForCache)
    {
        var parsed = Parse(line.Trim());
        if (string.IsNullOrEmpty(parsed.Url)) return null;
        if (!string.IsNullOrEmpty(parsed.Label))
            TitleCache.Set(userUrlForCache, parsed.Label!);
        if (!string.IsNullOrEmpty(parsed.Thumbnail))
            ThumbnailCache.Set(userUrlForCache, parsed.Thumbnail!, parsed.CropSquare);
        // live_status only appears on yt-dlp-backed sources; when present, it
        // overrides the cache default ("assume live") so non-live VODs become
        // seekable. Duration is set independently — a livestream may emit a
        // duration in some cases, but IsLive=true gates the seek UI off anyway.
        if (parsed.IsLive.HasValue) LiveStatusCache.Set(userUrlForCache, parsed.IsLive.Value);
        if (parsed.DurationSeconds > 0) DurationCache.Set(userUrlForCache, parsed.DurationSeconds);
        return parsed.Url;
    }

    public readonly record struct ParsedLine(
        string Url, string? Label, string? Thumbnail, bool CropSquare,
        bool? IsLive, double DurationSeconds);

    public static ParsedLine Parse(string line)
    {
        var parts = line.Split(Sep);
        if (parts.Length == 0) return new ParsedLine("", null, null, false, null, 0);
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
        // YouTube Music URLs and YouTube Topic-channel auto-generated music
        // videos both populate artist + track; the same family of sources is
        // also where YouTube's CDN pads square album art into a 16:9 frame.
        // Real video uploads don't populate these fields, so this is a tight
        // signal for "the bars on this thumbnail are container, not content".
        var cropSquare = Get(fields, "ARTIST").Length > 0
            && Get(fields, "TRACK").Length > 0;

        // Duration is a number-as-string; "NA" defaulted to "" by our
        // alternation operator parses as 0 and is filtered by DurationCache.Set.
        double durationSeconds = 0;
        var durRaw = Get(fields, "DUR");
        if (durRaw.Length > 0
            && double.TryParse(durRaw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            durationSeconds = d;
        }

        // live_status values from yt-dlp: not_live, is_live, is_upcoming,
        // was_live, post_live. Treat in-flight live states as live (no
        // transport); the rest become seekable VOD. When the field is
        // missing/empty/unknown (older yt-dlp, certain video types), fall
        // back on duration: a finite duration > 0 means a VOD by definition,
        // so default to non-live in that case. Without this fallback, any
        // source that didn't surface live_status would default to "assume
        // live" via the cache's default and lose all transport controls,
        // including skip-next.
        bool? isLive = Get(fields, "LIVE") switch
        {
            "is_live" or "is_upcoming" or "post_live" => true,
            "not_live" or "was_live" => false,
            _ => durationSeconds > 0 ? (bool?)false : null,
        };

        return new ParsedLine(
            url, BuildLabel(fields), thumb.Length > 0 ? thumb : null, cropSquare,
            isLive, durationSeconds);
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
