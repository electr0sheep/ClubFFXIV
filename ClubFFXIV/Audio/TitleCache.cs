using System.Collections.Concurrent;

namespace ClubFFXIV.Audio;

/// <summary>
/// Per-URL display title cache, populated by audio readers as part of their
/// CreateAsync (yt-dlp's --print title for Yt/Twitch/etc., icy-name + inline
/// StreamTitle metadata for direct HTTP/Icecast). The Now Playing header
/// reads it via Plugin.GetUrlTitle to render a friendlier label than the raw
/// URL. Lives for the plugin instance lifetime — a YouTube video looping
/// keeps its title without a re-fetch, and Icecast track changes overwrite
/// the same key as new metadata frames arrive.
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
