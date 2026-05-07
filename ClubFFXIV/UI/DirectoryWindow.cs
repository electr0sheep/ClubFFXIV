using System;
using System.Collections.Generic;
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

    // Per-column filter state. AND'd together when filtering rows; empty = no
    // constraint. Match is case-insensitive substring.
    private string nameFilter = "";
    private string locationFilter = "";
    private string descriptionFilter = "";
    private string urlFilter = "";

    private string statusMessage = "";
    private bool fetchKickoffPending;

    // Plot key of the most recently copied URL row. Drives the tooltip
    // confirmation ("Copied" vs "Click to copy") on the Stream URL cell.
    private string? lastCopiedPlotKey;

    // Column user IDs passed to TableSetupColumn and read back from
    // TableGetSortSpecs to know which column the user clicked. Values are
    // arbitrary but must be stable across frames.
    private const uint ColName = 1;
    private const uint ColLocation = 2;
    private const uint ColDescription = 3;
    private const uint ColUpdated = 4;
    private const uint ColUrl = 5;

    public DirectoryWindow(Plugin plugin)
        : base("ClubFFXIV — Public Directory##ClubFFXIVDirectory",
               ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(900, 540);
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
        DrawTable();
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

    private void DrawTable()
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

        var flags = ImGuiTableFlags.Resizable
                  | ImGuiTableFlags.Reorderable
                  | ImGuiTableFlags.Hideable
                  | ImGuiTableFlags.Sortable
                  | ImGuiTableFlags.RowBg
                  | ImGuiTableFlags.BordersOuter
                  | ImGuiTableFlags.BordersV
                  | ImGuiTableFlags.ScrollY
                  | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##dirTable", 5, flags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupColumn("Name",
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort,
            1.5f, ColName);
        ImGui.TableSetupColumn("Location",
            ImGuiTableColumnFlags.WidthStretch,
            1.8f, ColLocation);
        ImGui.TableSetupColumn("Description",
            ImGuiTableColumnFlags.WidthStretch,
            2.5f, ColDescription);
        ImGui.TableSetupColumn("Updated",
            ImGuiTableColumnFlags.WidthFixed,
            90f, ColUpdated);
        ImGui.TableSetupColumn("Stream URL",
            ImGuiTableColumnFlags.WidthStretch,
            2.0f, ColUrl);

        // Pin both the header row and the filter row so they remain visible
        // while the user scrolls through clubs.
        ImGui.TableSetupScrollFreeze(0, 2);
        ImGui.TableHeadersRow();

        // Filter row — manually rendered below the auto-generated headers so
        // each column gets its own substring filter input. The Updated column
        // has no filter cell (numeric/relative time).
        ImGui.TableNextRow();
        DrawFilterCell("##fName", ref nameFilter);
        DrawFilterCell("##fLoc", ref locationFilter);
        DrawFilterCell("##fDesc", ref descriptionFilter);
        ImGui.TableNextColumn(); // Updated — no filter
        DrawFilterCell("##fUrl", ref urlFilter);

        // Filter then sort. Dataset is small (registry directory rarely tops
        // a few hundred entries) — re-running every frame is cheap and keeps
        // the code straightforward.
        var rows = new List<DirectoryListingEntry>(cache.Clubs.Count);
        foreach (var club in cache.Clubs)
            if (MatchesFilters(club)) rows.Add(club);

        SortRows(rows);

        foreach (var club in rows)
            DrawRow(club);

        ImGui.EndTable();
    }

    private static void DrawFilterCell(string id, ref string value)
    {
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText(id, ref value, 64);
    }

    private bool MatchesFilters(DirectoryListingEntry e)
    {
        if (nameFilter.Length > 0
            && !e.DisplayName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (locationFilter.Length > 0
            && !FormatPlotKeyLocation(e.PlotKey).Contains(locationFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (descriptionFilter.Length > 0
            && !e.Description.Contains(descriptionFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (urlFilter.Length > 0
            && !e.StreamUrl.Contains(urlFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private void SortRows(List<DirectoryListingEntry> rows)
    {
        var specs = ImGui.TableGetSortSpecs();
        uint key = ColName;
        var ascending = true;
        if (specs.SpecsCount > 0)
        {
            var spec = specs.Specs;
            key = spec.ColumnUserID;
            ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
        }

        rows.Sort((a, b) =>
        {
            var cmp = key switch
            {
                ColName => string.Compare(a.DisplayName, b.DisplayName,
                                          StringComparison.OrdinalIgnoreCase),
                ColLocation => string.Compare(
                    FormatPlotKeyLocation(a.PlotKey),
                    FormatPlotKeyLocation(b.PlotKey),
                    StringComparison.OrdinalIgnoreCase),
                ColDescription => string.Compare(a.Description, b.Description,
                                                 StringComparison.OrdinalIgnoreCase),
                ColUpdated => a.UpdatedAt.CompareTo(b.UpdatedAt),
                ColUrl => string.Compare(a.StreamUrl, b.StreamUrl,
                                         StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
            return ascending ? cmp : -cmp;
        });
    }

    private void DrawRow(DirectoryListingEntry club)
    {
        ImGui.TableNextRow();
        ImGui.PushID("dir-" + club.PlotKey);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(club.DisplayName);

        ImGui.TableNextColumn();
        ImGui.TextWrapped(FormatPlotKeyLocation(club.PlotKey));

        ImGui.TableNextColumn();
        if (!string.IsNullOrWhiteSpace(club.Description))
        {
            // Long descriptions wrap inside the cell; the URL permission
            // prompt shows the full text when the listener actually picks Play.
            var desc = club.Description.Length > 240
                ? club.Description[..237] + "..."
                : club.Description;
            ImGui.TextWrapped(desc);
        }
        else
        {
            ImGui.TextDisabled("—");
        }

        ImGui.TableNextColumn();
        var ago = DateTime.UtcNow
                - DateTimeOffset.FromUnixTimeMilliseconds(club.UpdatedAt).UtcDateTime;
        ImGui.TextUnformatted(FormatAgo(ago));

        ImGui.TableNextColumn();
        if (club.StreamUrl.Length > 0)
        {
            // Click anywhere in the cell copies the URL. Tooltip on hover
            // doubles as the affordance ("Click to copy") and the success
            // confirmation ("Copied") for the most recently copied row.
            var urlPreview = club.StreamUrl.Length > 60
                ? club.StreamUrl[..57] + "..."
                : club.StreamUrl;
            if (ImGui.Selectable(urlPreview))
            {
                ImGui.SetClipboardText(club.StreamUrl);
                lastCopiedPlotKey = club.PlotKey;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(lastCopiedPlotKey == club.PlotKey
                    ? "Copied"
                    : "Click to copy");
            }
        }
        else
        {
            ImGui.TextDisabled("—");
        }

        ImGui.PopID();
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
