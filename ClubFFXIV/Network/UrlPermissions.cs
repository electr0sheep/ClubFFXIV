using System;

namespace ClubFFXIV.Network;

public enum UrlDecision
{
    Allow,
    Block,
    Ask,
}

/// <summary>
/// Optional context attached to a permission prompt or playback request so
/// the URL-permission popup can show "this URL is for: {ClubName}" plus the
/// DJ's description. Empty fields render as if no context were provided.
/// </summary>
public readonly record struct ClubContext(string ClubName, string Description)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ClubName) && string.IsNullOrWhiteSpace(Description);
}

/// <summary>
/// Decides whether a stream URL is allowed to play, blocked, or needs the
/// user's explicit permission. Exact-URL allow/block beats domain allow/block;
/// allow beats block within the same precedence tier.
/// </summary>
public sealed class UrlPermissions
{
    private readonly Configuration config;

    public UrlPermissions(Configuration config) { this.config = config; }

    public UrlDecision Check(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return UrlDecision.Block;

        if (config.AllowedUrls.Contains(url)) return UrlDecision.Allow;
        if (config.BlockedUrls.Contains(url)) return UrlDecision.Block;

        var host = ExtractHost(url);
        if (host != null)
        {
            if (MatchesAnyDomain(config.AllowedDomains, host)) return UrlDecision.Allow;
            if (MatchesAnyDomain(config.BlockedDomains, host)) return UrlDecision.Block;
        }

        return UrlDecision.Ask;
    }

    /// <summary>
    /// Suffix-aware domain match: an entry "youtube.com" matches a host of
    /// "youtube.com" exactly OR any subdomain ("www.youtube.com",
    /// "m.youtube.com"). Treats users adding a domain to the allow / block
    /// list the way they intuitively read it — Uri.Host is host-exact, but
    /// the UI label and the wizard's pre-approval list use the bare apex
    /// form ("youtube.com") and most stream URLs land on "www.youtube.com".
    /// Leading dots on the entry are stripped so ".youtube.com" works the
    /// same as "youtube.com" (defensive: a power user typing into the
    /// Permissions tab may include one).
    /// </summary>
    public static bool DomainMatches(string entry, string host)
    {
        var normalized = entry.TrimStart('.');
        if (string.IsNullOrEmpty(normalized)) return false;
        if (host.Length == normalized.Length)
            return string.Equals(host, normalized, StringComparison.OrdinalIgnoreCase);
        return host.Length > normalized.Length
            && host[host.Length - normalized.Length - 1] == '.'
            && host.EndsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAnyDomain(System.Collections.Generic.HashSet<string> domains, string host)
    {
        foreach (var entry in domains)
            if (DomainMatches(entry, host)) return true;
        return false;
    }

    public void AllowUrl(string url)
    {
        config.AllowedUrls.Add(url);
        config.BlockedUrls.Remove(url);
        config.Save();
    }

    public void AllowDomain(string url)
    {
        var host = ExtractHost(url);
        if (host == null) return;
        config.AllowedDomains.Add(host);
        config.BlockedDomains.Remove(host);
        config.Save();
    }

    public void BlockUrl(string url)
    {
        config.BlockedUrls.Add(url);
        config.AllowedUrls.Remove(url);
        config.Save();
    }

    public void BlockDomain(string url)
    {
        var host = ExtractHost(url);
        if (host == null) return;
        config.BlockedDomains.Add(host);
        config.AllowedDomains.Remove(host);
        config.Save();
    }

    public static string? ExtractHost(string url)
    {
        try { return new Uri(url).Host; }
        catch { return null; }
    }
}
