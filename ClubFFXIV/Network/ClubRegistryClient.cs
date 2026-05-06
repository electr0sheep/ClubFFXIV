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

    public async Task<ClubRecord?> GetAsync(string plotKey, CancellationToken ct = default)
    {
        var url = $"{baseUrl}/clubs/{Uri.EscapeDataString(plotKey)}";
        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
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
    [JsonPropertyName("streamUrl")] public string StreamUrl { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("djId")] public string DjId { get; set; } = "";
    [JsonPropertyName("door")] public DoorPayload? Door { get; set; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    [JsonPropertyName("listed")] public bool Listed { get; set; } = true;
}

public sealed class DirectoryListing
{
    [JsonPropertyName("clubs")] public List<DirectoryListingEntry> Clubs { get; set; } = new();
}

public sealed class DirectoryListingEntry
{
    [JsonPropertyName("plotKey")] public string PlotKey { get; set; } = "";
    [JsonPropertyName("streamUrl")] public string StreamUrl { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("djId")] public string DjId { get; set; } = "";
    [JsonPropertyName("door")] public DoorPayload? Door { get; set; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
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
    [JsonPropertyName("streamUrl")] public string StreamUrl { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("djId")] public string DjId { get; set; } = "";
    [JsonPropertyName("door")] public DoorPayload Door { get; set; } = new();
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
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
}

internal sealed class DeleteRequest
{
    [JsonPropertyName("nonce")] public long Nonce { get; set; }
}
