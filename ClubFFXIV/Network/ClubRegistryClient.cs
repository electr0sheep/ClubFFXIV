using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ClubFFXIV.Network;

public sealed class ClubRegistryClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string baseUrl;

    public ClubRegistryClient(string baseUrl)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClubFFXIV/0.1");
    }

    /// <summary>
    /// Cheap syntactic check: does the input even look like an http(s) base
    /// URL we could probe? Blank is accepted as "registry disabled". Anything
    /// past this gate still has to pass <see cref="CheckHealthAsync"/> to
    /// count as a real registry.
    /// </summary>
    public static bool TryNormalizeRegistryUrl(string? input, out string normalized)
    {
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0)
        {
            normalized = "";
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment))
        {
            normalized = trimmed.TrimEnd('/');
            return true;
        }

        normalized = "";
        return false;
    }

    /// <summary>
    /// Probe the configured base URL to confirm it's actually a ClubFFXIV
    /// registry (not just any reachable HTTP server). Hits <c>/health</c>,
    /// which the worker returns as the literal body <c>"ok"</c>. Throws on
    /// any other response — a generic 404, a different service's HTML, etc.
    /// — so the caller can surface a single error to the user instead of
    /// silently saving a bad URL and letting the per-tick ward fetcher spam
    /// the log.
    /// </summary>
    public async Task CheckHealthAsync(CancellationToken ct = default)
    {
        var url = $"{baseUrl}/health";
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Server returned {(int)resp.StatusCode}; this URL doesn't look like a ClubFFXIV registry.");
        }
        var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        if (!string.Equals(body, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "URL is reachable but doesn't appear to be a ClubFFXIV registry.");
        }
    }

    /// <summary>
    /// Fetch a club record by plot key. Two listener flows for password
    /// protection share this entry point:
    ///   - v2 encrypted: response carries <c>EncUrl</c>, <c>EncNonce</c>,
    ///     <c>PasswordSalt</c>; caller decrypts client-side via
    ///     <c>Passphrase.DecryptUrl</c>. The <paramref name="passwordHash"/>
    ///     argument is unused here.
    ///   - Legacy hash-gated: response carries <c>PasswordRequired=true</c>
    ///     and <c>PasswordSalt</c> only; caller derives the Argon2id hash
    ///     from the entered passphrase and re-invokes this method with it
    ///     to receive the plaintext URL. A wrong hash returns 401 with the
    ///     same metadata; this method maps that to a non-null record with
    ///     <c>StreamUrl</c> still empty so the upstream code path stays
    ///     uniform.
    /// </summary>
    public async Task<ClubRecord?> GetAsync(string plotKey, string? passwordHash = null, CancellationToken ct = default)
    {
        var url = $"{baseUrl}/clubs/{Uri.EscapeDataString(plotKey)}";
        if (!string.IsNullOrEmpty(passwordHash))
            url += $"?passwordHash={Uri.EscapeDataString(passwordHash)}";

        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        // 401 = password required or wrong password. Body is still a ClubRecord
        // (without streamUrl) so the caller can extract the salt and re-prompt.
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var unauthBody = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<ClubRecord>(unauthBody);
        }
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ClubRecord>(json);
    }

    public async Task<WardListing> GetWardAsync(
        uint worldId, uint territoryType, int ward, CancellationToken ct = default)
    {
        var url = $"{baseUrl}/wards/{worldId}/{territoryType}/{ward}";
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<WardListing>(json) ?? new WardListing();
    }

    public async Task PublishAsync(
        string plotKey,
        string streamUrl,
        string displayName,
        DjIdentity dj,
        DoorPayload? door = null,
        bool listed = true,
        string description = "",
        string? passwordSalt = null,
        string? encUrl = null,
        string? encNonce = null,
        int? schemaVersion = null,
        CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new PublishRequest
        {
            StreamUrl = streamUrl,
            DisplayName = displayName,
            Description = description,
            Nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Door = door,
            Listed = listed,
            // For v2 encrypted publish: PasswordSalt + EncUrl + EncNonce +
            // SchemaVersion=2; StreamUrl must be empty. For unprotected:
            // all four password-related params null. The registry's
            // passwordFieldsError rejects any other combination.
            PasswordSalt = passwordSalt,
            EncUrl = encUrl,
            EncNonce = encNonce,
            SchemaVersion = schemaVersion,
        });
        var signature = dj.Sign($"POST:{plotKey}:{body}");

        var url = $"{baseUrl}/clubs/{Uri.EscapeDataString(plotKey)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-pubkey", dj.PublicKeyBase64);
        req.Headers.Add("x-signature", signature);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            throw ParseRegistryError(resp.StatusCode, respBody, "Publish");
        }
    }

    public async Task<DirectoryListing> GetDirectoryAsync(CancellationToken ct = default)
    {
        var url = $"{baseUrl}/clubs";
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DirectoryListing>(json) ?? new DirectoryListing();
    }

    public async Task DeleteAsync(string plotKey, DjIdentity dj, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new DeleteRequest
        {
            Nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        var signature = dj.Sign($"DELETE:{plotKey}:{body}");

        var url = $"{baseUrl}/clubs/{Uri.EscapeDataString(plotKey)}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-pubkey", dj.PublicKeyBase64);
        req.Headers.Add("x-signature", signature);

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode)
        {
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            throw ParseRegistryError(resp.StatusCode, respBody, "Delete");
        }
    }

    public void Dispose() => http.Dispose();

    /// <summary>
    /// Translate an HTTP error body (typically `{ error, code?, retryAfterSeconds? }`)
    /// into a clean exception whose <c>Message</c> is suitable for the existing
    /// UI catch-sites that format as <c>"$action failed: {ex.Message}"</c> — i.e.
    /// the message itself does NOT prefix the action. For known transient codes
    /// (QUOTA_EXHAUSTED / RATE_LIMITED) the server's message is surfaced verbatim
    /// plus a humanized retry hint. <paramref name="action"/> is only used in the
    /// fallback case where the body wasn't structured JSON.
    /// </summary>
    private static Exception ParseRegistryError(HttpStatusCode status, string body, string action)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<RegistryErrorBody>(body);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Error))
                {
                    var retry = parsed.RetryAfterSeconds is { } secs && secs > 0
                        ? TimeSpan.FromSeconds(secs)
                        : (TimeSpan?)null;
                    if (parsed.Code == "QUOTA_EXHAUSTED" || parsed.Code == "RATE_LIMITED")
                    {
                        var hint = retry.HasValue ? $" (try again in {FormatRetry(retry.Value)})" : "";
                        return new RegistryException(parsed.Error + hint, parsed.Code, retry);
                    }
                    return new RegistryException(parsed.Error, parsed.Code, retry);
                }
            }
            catch (JsonException)
            {
                // Body wasn't JSON — fall through to the raw-body format below.
            }
        }
        // Body wasn't structured. Stay terse so the catch-site's
        // "$action failed: {ex.Message}" wrapper reads cleanly.
        _ = action;
        var snippet = string.IsNullOrEmpty(body) ? "" : $": {body}";
        return new InvalidOperationException($"server returned {(int)status}{snippet}");
    }

    private static string FormatRetry(TimeSpan ts)
    {
        if (ts.TotalSeconds < 90) return $"about {Math.Max(1, (int)ts.TotalSeconds)} seconds";
        if (ts.TotalMinutes < 90) return $"about {Math.Max(1, (int)ts.TotalMinutes)} minutes";
        return $"about {Math.Max(1, (int)ts.TotalHours)} hours";
    }
}

/// <summary>
/// Thrown by <see cref="ClubRegistryClient"/> when the backend returns a
/// structured error body. Exposes the server's <see cref="Code"/> (e.g.
/// "QUOTA_EXHAUSTED") and the <see cref="RetryAfter"/> hint so callers can
/// distinguish "transient, retry later" from "you broke the request."
/// </summary>
public sealed class RegistryException : Exception
{
    public string? Code { get; }
    public TimeSpan? RetryAfter { get; }

    public RegistryException(string message, string? code = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        Code = code;
        RetryAfter = retryAfter;
    }

    public bool IsTransient => Code is "QUOTA_EXHAUSTED" or "RATE_LIMITED";
}

internal sealed class RegistryErrorBody
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("retryAfterSeconds")] public int? RetryAfterSeconds { get; set; }
}

public sealed class ClubRecord
{
    // Empty for v2 encrypted records (the URL travels in EncUrl) and for
    // legacy password-protected records the caller hasn't authenticated to
    // yet. PasswordRequired distinguishes "no URL set" from "URL is gated."
    [JsonPropertyName("streamUrl")] public string StreamUrl { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("djId")] public string DjId { get; set; } = "";
    [JsonPropertyName("door")] public DoorPayload? Door { get; set; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    [JsonPropertyName("listed")] public bool Listed { get; set; } = true;
    // Password gating. PasswordRequired=true with no EncUrl signals legacy
    // records that need GetAsync(passwordHash:); with EncUrl set, the
    // listener decrypts client-side via Passphrase.DecryptUrl.
    [JsonPropertyName("passwordRequired")] public bool PasswordRequired { get; set; }
    [JsonPropertyName("passwordSalt")] public string? PasswordSalt { get; set; }
    // v2 fields. Both present + SchemaVersion=2 indicates a fully-encrypted
    // record; absent means a legacy hash-gated record. Mixed states are
    // rejected by the registry on POST.
    [JsonPropertyName("encUrl")] public string? EncUrl { get; set; }
    [JsonPropertyName("encNonce")] public string? EncNonce { get; set; }
    [JsonPropertyName("schemaVersion")] public int? SchemaVersion { get; set; }
}

public sealed class DirectoryListing
{
    [JsonPropertyName("clubs")] public List<DirectoryListingEntry> Clubs { get; set; } = new();
}

public sealed class DirectoryListingEntry
{
    [JsonPropertyName("plotKey")] public string PlotKey { get; set; } = "";
    // Empty for password-protected clubs (URL is only released after auth).
    [JsonPropertyName("streamUrl")] public string StreamUrl { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("djId")] public string DjId { get; set; } = "";
    [JsonPropertyName("door")] public DoorPayload? Door { get; set; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    [JsonPropertyName("passwordRequired")] public bool PasswordRequired { get; set; }
}

public sealed class WardListing
{
    [JsonPropertyName("worldId")] public uint WorldId { get; set; }
    [JsonPropertyName("territoryType")] public uint TerritoryType { get; set; }
    [JsonPropertyName("ward")] public int Ward { get; set; }
    [JsonPropertyName("clubs")] public List<WardListingEntry> Clubs { get; set; } = new();
}

public sealed class WardListingEntry
{
    [JsonPropertyName("plotKey")] public string PlotKey { get; set; } = "";
    // Empty for password-protected clubs (URL is only released after auth).
    [JsonPropertyName("streamUrl")] public string StreamUrl { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("djId")] public string DjId { get; set; } = "";
    [JsonPropertyName("door")] public DoorPayload Door { get; set; } = new();
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    [JsonPropertyName("passwordRequired")] public bool PasswordRequired { get; set; }
}

public sealed class DoorPayload
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }
    [JsonPropertyName("territoryType")] public uint TerritoryType { get; set; }
    [JsonPropertyName("ward")] public int Ward { get; set; }
}

internal sealed class PublishRequest
{
    [JsonPropertyName("streamUrl")] public string StreamUrl { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("nonce")] public long Nonce { get; set; }
    [JsonPropertyName("door"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DoorPayload? Door { get; set; }
    [JsonPropertyName("listed")] public bool Listed { get; set; } = true;
    // Three valid combinations on the wire (mirroring backend's
    // passwordFieldsError):
    //   1. all null — unprotected publish.
    //   2. PasswordSalt + EncUrl + EncNonce + SchemaVersion=2 — v2 encrypted.
    //      StreamUrl must be empty.
    //   3. PasswordSalt + PasswordHash — legacy auth-hash. Plugin no longer
    //      writes this on new publishes, but the field stays for back-compat
    //      with any downstream tooling that constructs requests directly.
    [JsonPropertyName("passwordSalt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordSalt { get; set; }
    [JsonPropertyName("passwordHash"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordHash { get; set; }
    [JsonPropertyName("encUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncUrl { get; set; }
    [JsonPropertyName("encNonce"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncNonce { get; set; }
    [JsonPropertyName("schemaVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SchemaVersion { get; set; }
}

internal sealed class DeleteRequest
{
    [JsonPropertyName("nonce")] public long Nonce { get; set; }
}
