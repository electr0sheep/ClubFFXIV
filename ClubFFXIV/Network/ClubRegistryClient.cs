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
        CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new PublishRequest
        {
            StreamUrl = streamUrl,
            DisplayName = displayName,
            Nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Door = door,
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
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Publish failed ({(int)resp.StatusCode}): {err}");
        }
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
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Delete failed ({(int)resp.StatusCode}): {err}");
        }
    }

    public void Dispose() => http.Dispose();
}

public sealed class ClubRecord
{
    [JsonPropertyName("streamUrl")] public string StreamUrl { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
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
    [JsonPropertyName("nonce")] public long Nonce { get; set; }
    [JsonPropertyName("door"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DoorPayload? Door { get; set; }
}

internal sealed class DeleteRequest
{
    [JsonPropertyName("nonce")] public long Nonce { get; set; }
}
