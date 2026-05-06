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
            if (config.AllowedDomains.Contains(host)) return UrlDecision.Allow;
            if (config.BlockedDomains.Contains(host)) return UrlDecision.Block;
        }

        return UrlDecision.Ask;
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
