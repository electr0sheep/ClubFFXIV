using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ClubFFXIV.Game;
using ClubFFXIV.Network;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// Browse the public directory of registered clubs. Opens via the
/// "Browse Public Directory" button in /pclub config or via /pclub directory.
/// Lives in its own window so the main config window stays focused on the
/// user's own setup. Lazily fetches on first open and caches via
/// <see cref="Plugin.FetchDirectoryAsync"/>; the user can manually refresh.
/// </summary>
public sealed class DirectoryWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string searchQuery = "";
    private string statusMessage = "";
    private bool fetchKickoffPending;
    private DirectorySort sortMode = DirectorySort.Name;

    public enum DirectorySort
    {
        Name,
        RecentlyUpdated,
        World,
    }

    public DirectoryWindow(Plugin plugin)
        : base("ClubFFXIV — Public Directory##ClubFFXIVDirectory",
               ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(620, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        // Kick off the first fetch the moment the user opens the window. If
        // the cache is fresh from a recent open, FetchDirectoryAsync no-ops
        // inside.
        if (plugin.DirectoryCache == null
            && !plugin.DirectoryFetchInFlight
            && !fetchKickoffPending
            && plugin.RegistryEnabled)
        {
            KickoffFetch(force: false);
        }
    }

    public override void Draw()
    {
        if (!plugin.RegistryEnabled)
        {
            ImGui.TextWrapped(
                "Registry URL is not set. Configure one in /pclub config → Registry " +
                "to browse the public directory.");
            return;
        }

        DrawHeader();
        ImGui.Spacing();
        DrawSearch();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawList();
    }

    private void DrawHeader()
    {
        if (ImGui.Button("Refresh"))
        {
            if (!plugin.DirectoryFetchInFlight && !fetchKickoffPending)
                KickoffFetch(force: true);
        }

        var cache = plugin.DirectoryCache;
        if (cache != null)
        {
            ImGui.SameLine();
            var ago = DateTime.UtcNow - plugin.DirectoryCacheFetchedAt;
            var summary = cache.Clubs.Count == 1
                ? "1 club listed"
                : $"{cache.Clubs.Count} clubs listed";
            ImGui.TextDisabled($"{summary}  ·  fetched {FormatAgo(ago)}");
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(" · " + statusMessage);
        }
    }

    private void DrawSearch()
    {
        ImGui.TextUnformatted("Search:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-280);
        ImGui.InputText("##dirSearch", ref searchQuery, 128);
        ImGui.SameLine();
        if (ImGui.Button("Clear", new Vector2(70, 0)))
            searchQuery = "";
        ImGui.SameLine();
        ImGui.TextUnformatted("Sort:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(125);
        if (ImGui.BeginCombo("##dirSort", SortLabel(sortMode)))
        {
            foreach (var v in Enum.GetValues<DirectorySort>())
            {
                if (ImGui.Selectable(SortLabel(v), v == sortMode))
                    sortMode = v;
            }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled(
            "  Search matches club name, description, location, or stream URL.");
    }

    private static string SortLabel(DirectorySort s) => s switch
    {
        DirectorySort.Name => "Name (A→Z)",
        DirectorySort.RecentlyUpdated => "Recently updated",
        DirectorySort.World => "World",
        _ => s.ToString(),
    };

    private void DrawList()
    {
        var cache = plugin.DirectoryCache;
        if (cache == null)
        {
            ImGui.TextDisabled("Loading...");
            return;
        }

        if (cache.Clubs.Count == 0)
        {
            ImGui.TextDisabled("No clubs are currently listed in the directory.");
            return;
        }

        var query = searchQuery.Trim();
        var hasQuery = query.Length > 0;
        var queryLower = hasQuery ? query.ToLowerInvariant() : "";

        ImGui.BeginChild("##dirList", new Vector2(-1, -1), true);

        var matchCount = 0;
        foreach (var club in SortClubs(cache.Clubs, sortMode))
        {
            if (hasQuery && !MatchesSearch(club, queryLower)) continue;
            matchCount++;
            DrawRow(club);
        }

        if (hasQuery && matchCount == 0)
            ImGui.TextDisabled($"No clubs matched \"{query}\".");

        ImGui.EndChild();
    }

    private static IEnumerable<DirectoryListingEntry> SortClubs(
        List<DirectoryListingEntry> clubs, DirectorySort mode) => mode switch
    {
        DirectorySort.Name => clubs.OrderBy(
            c => c.DisplayName, StringComparer.OrdinalIgnoreCase),
        DirectorySort.RecentlyUpdated => clubs.OrderByDescending(c => c.UpdatedAt),
        DirectorySort.World => clubs
            .OrderBy(c => ExtractWorldId(c.PlotKey))
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase),
        _ => clubs,
    };

    private static uint ExtractWorldId(string plotKey) =>
        PlotKey.TryParse(plotKey, out var key) ? key.WorldId : uint.MaxValue;

    private void DrawRow(DirectoryListingEntry club)
    {
        ImGui.PushID("dir-" + club.PlotKey);

        ImGui.TextUnformatted(club.DisplayName);
        ImGui.TextDisabled("  " + FormatPlotKeyLocation(club.PlotKey));

        if (!string.IsNullOrWhiteSpace(club.Description))
        {
            // Truncated for the row; full text shows in the URL permission prompt
            // when the user picks Play.
            var desc = club.Description.Length > 280
                ? club.Description[..277] + "..."
                : club.Description;
            ImGui.Indent(12f);
            ImGui.TextWrapped(desc);
            ImGui.Unindent(12f);
        }

        if (ImGui.SmallButton("Play"))
        {
            try
            {
                plugin.Config.LastStreamUrl = club.StreamUrl;
                plugin.Config.Save();
                plugin.PlayStream(
                    club.StreamUrl,
                    new ClubContext(club.DisplayName, club.Description));
                statusMessage = $"Playing: {club.DisplayName}";
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Directory Play failed");
                statusMessage = $"Error: {ex.Message}";
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy URL"))
        {
            ImGui.SetClipboardText(club.StreamUrl);
            statusMessage = "URL copied.";
        }

        ImGui.TextDisabled("  " + (club.StreamUrl.Length > 80
            ? club.StreamUrl[..77] + "..."
            : club.StreamUrl));

        ImGui.PopID();
        ImGui.Spacing();
    }

    private bool MatchesSearch(DirectoryListingEntry e, string queryLower)
    {
        if (e.DisplayName.ToLowerInvariant().Contains(queryLower)) return true;
        if (e.Description.ToLowerInvariant().Contains(queryLower)) return true;
        if (e.StreamUrl.ToLowerInvariant().Contains(queryLower)) return true;
        // Decoded location: "World 1234 — Mist Ward 1, Plot 12"
        if (FormatPlotKeyLocation(e.PlotKey).ToLowerInvariant().Contains(queryLower)) return true;
        return false;
    }

    private string FormatPlotKeyLocation(string plotKeyCanonical)
    {
        if (!PlotKey.TryParse(plotKeyCanonical, out var key))
            return plotKeyCanonical;
        var world = plugin.HousingDetector.LookupWorldName(key.WorldId);
        var dc = plugin.HousingDetector.LookupDataCenterName(key.WorldId);
        var worldDc = string.IsNullOrEmpty(dc) ? world : $"{world} ({dc})";
        var district = plugin.HousingDetector.GetDisplayName(key);
        return $"{worldDc} — {district}";
    }

    private void KickoffFetch(bool force)
    {
        fetchKickoffPending = true;
        statusMessage = "Loading...";
        _ = Task.Run(async () =>
        {
            try
            {
                await plugin.FetchDirectoryAsync(force);
                statusMessage = "";
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Directory fetch failed: {ex.Message}");
                statusMessage = $"Fetch failed: {ex.Message}";
            }
            finally
            {
                fetchKickoffPending = false;
            }
        });
    }

    private static string FormatAgo(TimeSpan ago)
    {
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} h ago";
        return $"{(int)ago.TotalDays} d ago";
    }
}
