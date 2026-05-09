using System;
using System.Collections.Generic;
using System.Net;

namespace ClubFFXIV.Network;

/// <summary>
/// Plugin-side preflight syntax check for stream URLs. Mirrors the syntax
/// checks the registry runs in <c>backend/src/streamUrlValidation.ts</c> so
/// the DJ gets an immediate, actionable error instead of a delayed registry
/// rejection.
///
/// For password-protected publishes the registry can't see the URL (it's
/// AES-256-GCM-encrypted client-side), so this is the only validation the
/// URL ever gets. For unprotected publishes the registry still runs the
/// HEAD/Range probe — this layer is defense-in-depth, not a replacement.
///
/// Limited to syntax (scheme, length, credentials, blocked/private hosts).
/// We don't probe — that would block the publish UI thread on a network
/// round-trip, and the registry probe already covers it for unencrypted
/// publishes.
/// </summary>
public static class StreamUrlValidator
{
    private const int MaxUrlLength = 512;

    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "ip6-localhost", "ip6-loopback", "broadcasthost",
    };

    /// <summary>
    /// Returns <c>null</c> if the URL passes all syntax checks, otherwise
    /// a user-facing error string. Same shape as the backend's
    /// <c>validateStreamUrlSyntax</c> so the messaging stays consistent
    /// regardless of which side caught the bad input.
    /// </summary>
    public static string? Validate(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "Stream URL cannot be empty.";
        if (url.Length > MaxUrlLength) return $"Stream URL too long (max {MaxUrlLength}).";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return "Stream URL is not a valid URL.";
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return "Stream URL must be http or https.";
        if (!string.IsNullOrEmpty(parsed.UserInfo))
            return "Stream URL cannot contain credentials.";

        var host = NormalizeHost(parsed.Host);
        if (host.Length == 0) return "Stream URL host is empty.";
        if (BlockedHosts.Contains(host))
            return "Stream URL host is not allowed (loopback).";
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && IsPrivateIPv4(ip))
                return "Stream URL host is not allowed (private/loopback IPv4).";
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && IsPrivateIPv6(ip))
                return "Stream URL host is not allowed (private/loopback IPv6).";
        }
        return null;
    }

    private static string NormalizeHost(string host)
    {
        var h = host.ToLowerInvariant();
        // Uri.Host already strips brackets from IPv6 literals, but be defensive.
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        return h;
    }

    private static bool IsPrivateIPv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        // Mirrors backend's isPrivateIPv4: 0/8, 10/8, 127/8, 169.254/16,
        // 172.16-31/12, 192.168/16, 100.64-127/10 (CGNAT), 224+/multicast.
        if (b[0] == 0) return true;
        if (b[0] == 10) return true;
        if (b[0] == 127) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
        if (b[0] >= 224) return true;
        return false;
    }

    private static bool IsPrivateIPv6(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;          // ::1
        if (ip.IsIPv6LinkLocal) return true;                 // fe80::/10
        if (ip.IsIPv6SiteLocal) return true;                 // deprecated fec0::/10 — block anyway
        if (ip.IsIPv6Multicast) return true;
        // Unique local addresses (fc00::/7) — IPAddress doesn't expose a flag,
        // check the first byte directly. RFC 4193.
        var b = ip.GetAddressBytes();
        if ((b[0] & 0xFE) == 0xFC) return true;
        // IPv4-mapped IPv6 (::ffff:a.b.c.d) — recurse into the IPv4 check.
        if (ip.IsIPv4MappedToIPv6)
            return IsPrivateIPv4(ip.MapToIPv4());
        return false;
    }
}
