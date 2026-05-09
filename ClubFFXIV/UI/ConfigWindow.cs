using System;
using System.Numerics;
using System.Threading.Tasks;
using ClubFFXIV.Game;
using ClubFFXIV.Network;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string registryUrlInput;
    // Local edit buffer for the yt-dlp cookies-from-browser knob. Synced
    // from Config on construction and committed back on lose-focus
    // (IsItemDeactivatedAfterEdit) so we don't write the config file on
    // every keystroke.
    private string ytDlpCookiesInput;

    // Inline-progress text for in-flight async operations ("Publishing...",
    // "Saving...", "Unpublishing..."). Final results go to Dalamud notifications;
    // this field only holds the during-the-network-call indicator so the user
    // sees something between click and toast.
    private string inflightStatus = "";

    // Per-column substring filters for the My Houses table (My Clubs tab).
    // AND'd together; empty = no constraint. State and Calibrated columns are
    // sort-only — small enumerated value sets that don't benefit from typing.
    private string houseNameFilter = "";
    private string houseDescriptionFilter = "";
    private string houseUrlFilter = "";

    // Plot key of the most recently copied URL row. Drives the "Click to copy"
    // ↔ "Copied" tooltip toggle on the My Houses Stream URL cell, mirroring
    // the directory window's behavior.
    private string? lastCopiedHouseKey;

    // Column user IDs for the My Houses table — passed to TableSetupColumn
    // and read back via TableGetSortSpecs. Values are arbitrary but stable.
    private const uint HCalib = 1;
    private const uint HName = 2;
    private const uint HState = 3;
    private const uint HDescription = 4;
    private const uint HUrl = 5;
    private const uint HActions = 6;

    public ConfigWindow(Plugin plugin)
        // ImGui ID (after ##) stays "ClubFFXIVConfig" so persisted window
        // position/size from prior versions still apply. Display title was
        // "ClubFFXIV"; "ClubFFXIV — Settings" makes the split with the
        // music player window unambiguous.
        : base("ClubFFXIV — Settings##ClubFFXIVConfig", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(600, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        registryUrlInput = plugin.Config.RegistryUrl;
        ytDlpCookiesInput = plugin.Config.YtDlpCookiesBrowser;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Now Playing surface (header + URL input + proximity) lives in
        // MusicPlayerWindow. This window holds the rest of the configuration:
        // club management, registry, playback/spatial tuning, advanced.
        if (ImGui.BeginTabBar("##mainTabs"))
        {
            if (ImGui.BeginTabItem("My Clubs"))
            {
                DrawMyClubsTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Registry"))
            {
                DrawRegistryTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Advanced"))
            {
                DrawAdvancedTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        // Inflight progress indicator at the bottom — only visible during
        // an active async op (Publishing... / Saving... / Unpublishing...).
        // Empty most of the time; final results go to Dalamud notifications.
        if (!string.IsNullOrEmpty(inflightStatus))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled(inflightStatus);
        }
    }

    private void DrawMyClubsTab()
    {
        DrawCurrentLocationSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawMyHousesSection();
    }

    private void DrawRegistryTab()
    {
        DrawRegistrySection();
    }

    private void DrawSettingsTab()
    {
        DrawPlaybackSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSpatialTuningSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawPermissionsSection();
    }

    private void DrawPlaybackSection()
    {
        ImGui.TextUnformatted("Playback");
        ImGui.Spacing();

        var loop = plugin.Config.LoopFinishedVideos;
        if (ImGui.Checkbox("Loop videos when they finish", ref loop))
        {
            plugin.Config.LoopFinishedVideos = loop;
            plugin.Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When a finite source ends, restart it from the top:\n" +
                "  • Single video: replays the same video.\n" +
                "  • Playlist: starts the playlist over (with a fresh\n" +
                "    shuffle if 'Random playlist order' is on).\n" +
                "Off = playlist plays through once and stops.\n" +
                "Indefinite streams (Twitch, Icecast) never reach a\n" +
                "natural end, so this setting is a no-op for them.");

        var random = plugin.Config.PlaylistRandom;
        if (ImGui.Checkbox("Random playlist order", ref random))
        {
            plugin.Config.PlaylistRandom = random;
            plugin.Config.Save();
            plugin.SyncYtDlpOptions();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "How playlist items are ordered:\n" +
                "  • Off: original playlist order.\n" +
                "  • On: yt-dlp shuffles the playlist before iteration.\n" +
                "No effect on single videos or live streams.");

        var showThumbs = plugin.Config.ShowNowPlayingThumbnails;
        if (ImGui.Checkbox("Show thumbnails in Now Playing", ref showThumbs))
        {
            plugin.Config.ShowNowPlayingThumbnails = showThumbs;
            plugin.Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Render album / video artwork beside each Now Playing row.\n" +
                "  • YouTube / SoundCloud / Twitch: thumbnail from yt-dlp.\n" +
                "  • Icecast / direct MP3: placeholder square (no artwork available).\n" +
                "Off = text-only rows.");

        var keepInSub = plugin.Config.KeepPlayingInLinkedSubterritories;
        if (ImGui.Checkbox("Keep playing in FC workshop / linked sub-rooms", ref keepInSub))
        {
            plugin.Config.KeepPlayingInLinkedSubterritories = keepInSub;
            plugin.Config.Save();
        }
    }

    private void DrawAdvancedTab()
    {
        DrawBinariesSection();
        ImGui.Spacing();
        DrawMultiStreamSection();
        ImGui.Spacing();
        DrawYtDlpSection();
    }

    private void DrawYtDlpSection()
    {
        ImGui.TextUnformatted("yt-dlp");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "If YouTube starts showing \"Sign in to confirm you're not a bot\" errors, " +
            "set this to \"firefox\". Make sure you're logged into YouTube in Firefox " +
            "first — yt-dlp will read your session cookies from there. Leave empty to " +
            "stay anonymous.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Cookies from browser:");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Recommended: firefox\n\n" +
                "Chromium-based browsers (Chrome, Edge, Brave,\n" +
                "Vivaldi, Opera) encrypt cookies with app-bound\n" +
                "encryption that yt-dlp cannot decrypt without\n" +
                "additional setup. Use Firefox.\n\n" +
                "You must be logged into YouTube (or whichever\n" +
                "site is gating you) in Firefox.\n\n" +
                "Saved on lose-focus.");

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##ytDlpCookies", ref ytDlpCookiesInput, 32);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            plugin.Config.YtDlpCookiesBrowser = ytDlpCookiesInput.Trim();
            plugin.Config.Save();
            plugin.SyncYtDlpOptions();
        }
    }

    private void DrawMultiStreamSection()
    {
        ImGui.TextUnformatted("Multi-stream");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Outdoor proximity keeps multiple nearby clubs streaming at once, " +
            "mixed by per-voice distance. Each yt-dlp source costs significant " +
            "CPU + memory, so the cap counts yt-dlp voices as 2.");
        ImGui.Spacing();

        var enabled = plugin.Config.MultiStreamEnabled;
        if (ImGui.Checkbox("Enable multi-stream outdoor mode", ref enabled))
        {
            plugin.Config.MultiStreamEnabled = enabled;
            plugin.Config.Save();
            plugin.OnMultiStreamToggled();
        }

        var cap = plugin.Config.MaxConcurrentStreams;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Max concurrent streams", ref cap, 1, 10))
        {
            plugin.Config.MaxConcurrentStreams = Math.Clamp(cap, 1, 10);
            plugin.Config.Save();
        }
    }

    private string newAllowDomainInput = "";
    private string newBlockDomainInput = "";

    private void DrawPermissionsSection()
    {
        ImGui.TextUnformatted("Permissions (allow / block lists)");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Streams from unfamiliar domains are blocked until you approve them. " +
            "Manage the allow / block lists here.");
        ImGui.Spacing();

        DrawList("Allowed domains", plugin.Config.AllowedDomains, ref newAllowDomainInput, "allow-d");
        DrawList("Blocked domains", plugin.Config.BlockedDomains, ref newBlockDomainInput, "block-d");
        DrawListReadOnly("Allowed URLs", plugin.Config.AllowedUrls, "allow-u");
        DrawListReadOnly("Blocked URLs", plugin.Config.BlockedUrls, "block-u");
    }

    private void DrawList(string label, System.Collections.Generic.HashSet<string> set,
                          ref string input, string idPrefix)
    {
        ImGui.TextUnformatted($"{label} ({set.Count})");
        string? toRemove = null;
        foreach (var item in set)
        {
            ImGui.PushID(idPrefix + "-" + item);
            ImGui.BulletText(item);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) toRemove = item;
            ImGui.PopID();
        }
        if (toRemove != null) { set.Remove(toRemove); plugin.Config.Save(); }

        ImGui.SetNextItemWidth(280);
        ImGui.InputText($"##{idPrefix}-add", ref input, 256);
        ImGui.SameLine();
        if (ImGui.Button($"Add##{idPrefix}-addbtn") && !string.IsNullOrWhiteSpace(input))
        {
            set.Add(input.Trim());
            plugin.Config.Save();
            input = "";
        }
        ImGui.Spacing();
    }

    private void DrawListReadOnly(string label, System.Collections.Generic.HashSet<string> set, string idPrefix)
    {
        ImGui.TextUnformatted($"{label} ({set.Count})");
        string? toRemove = null;
        foreach (var item in set)
        {
            ImGui.PushID(idPrefix + "-" + item);
            ImGui.TextDisabled("  " + (item.Length > 60 ? item[..57] + "..." : item));
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) toRemove = item;
            ImGui.PopID();
        }
        if (toRemove != null) { set.Remove(toRemove); plugin.Config.Save(); }
        ImGui.Spacing();
    }

    private string ytDlpVersion = "(checking...)";
    private string ffmpegVersion = "(checking...)";
    private string denoVersion = "(checking...)";
    private bool versionsRequested;

    private void DrawBinariesSection()
    {
        ImGui.TextUnformatted("External binaries (yt-dlp, ffmpeg, deno)");
        ImGui.Separator();
        ImGui.Spacing();

        // Lazy-resolve versions on first display so we don't spawn processes
        // on every plugin start.
        if (!versionsRequested)
        {
            versionsRequested = true;
            _ = Task.Run(async () =>
            {
                ytDlpVersion = await plugin.Binaries.GetYtDlpVersionAsync();
                ffmpegVersion = await plugin.Binaries.GetFfmpegVersionAsync();
                denoVersion = await plugin.Binaries.GetDenoVersionAsync();
            });
        }

        ImGui.TextWrapped(
            "Required for Twitch / YouTube / SoundCloud playback. " +
            "Direct MP3/Icecast streams don't need these. Deno is yt-dlp's " +
            "JavaScript runtime — needed to solve YouTube's signature/n-challenge.");
        ImGui.Spacing();

        DrawBinaryRow("yt-dlp", plugin.Binaries.YtDlpInstalled, ytDlpVersion, async () =>
        {
            ytDlpVersion = "(updating...)";
            try
            {
                ytDlpVersion = await plugin.Binaries.UpdateYtDlpAsync();
                plugin.Notify("ClubFFXIV", "yt-dlp updated.", NotificationType.Success);
            }
            catch (Exception ex)
            {
                ytDlpVersion = $"(error: {ex.Message})";
                plugin.Notify("ClubFFXIV: yt-dlp update failed",
                    ex.Message, NotificationType.Error, durationSeconds: 8);
            }
        });

        DrawBinaryRow("ffmpeg", plugin.Binaries.FfmpegInstalled, ffmpegVersion, async () =>
        {
            ffmpegVersion = "(updating, ~80 MB...)";
            try
            {
                ffmpegVersion = await plugin.Binaries.UpdateFfmpegAsync();
                plugin.Notify("ClubFFXIV", "ffmpeg updated.", NotificationType.Success);
            }
            catch (Exception ex)
            {
                ffmpegVersion = $"(error: {ex.Message})";
                plugin.Notify("ClubFFXIV: ffmpeg update failed",
                    ex.Message, NotificationType.Error, durationSeconds: 8);
            }
        });

        DrawBinaryRow("deno", plugin.Binaries.DenoInstalled, denoVersion, async () =>
        {
            denoVersion = "(updating, ~40 MB...)";
            try
            {
                denoVersion = await plugin.Binaries.UpdateDenoAsync();
                plugin.Notify("ClubFFXIV", "Deno updated.", NotificationType.Success);
            }
            catch (Exception ex)
            {
                denoVersion = $"(error: {ex.Message})";
                plugin.Notify("ClubFFXIV: Deno update failed",
                    ex.Message, NotificationType.Error, durationSeconds: 8);
            }
        });

        ImGui.Spacing();
        var auto = plugin.Config.AutoUpdateBinaries;
        if (ImGui.Checkbox("Auto-check for yt-dlp updates every 2 days", ref auto))
        {
            plugin.Config.AutoUpdateBinaries = auto;
            plugin.Config.Save();
        }

        if (plugin.Config.BinariesLastChecked != DateTime.MinValue)
        {
            var ago = DateTime.UtcNow - plugin.Config.BinariesLastChecked;
            ImGui.TextDisabled($"Last update check: {FormatAgo(ago)}");
        }

        if (!plugin.Binaries.Ready)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(
                "These are required for Twitch / YouTube / SoundCloud playback only. " +
                "Click Install above; nothing is downloaded automatically.");
        }
    }

    private void DrawBinaryRow(string name, bool installed, string version, Func<Task> onUpdate)
    {
        ImGui.PushID("bin-" + name);
        var icon = installed ? "✓" : "—";
        ImGui.TextUnformatted($"{icon} {name}:");
        ImGui.SameLine();
        ImGui.TextDisabled(version);
        ImGui.SameLine();
        var btnLabel = installed ? "Check / update" : "Install";
        if (ImGui.SmallButton(btnLabel))
            _ = Task.Run(onUpdate);
        ImGui.PopID();
    }

    private static string FormatAgo(TimeSpan ago)
    {
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} h ago";
        return $"{(int)ago.TotalDays} d ago";
    }

    private void DrawRegistrySection()
    {
        ImGui.TextUnformatted("Registry");
        ImGui.Spacing();
        ImGui.TextWrapped("Backend URL (e.g. https://registry.clubffxiv.workers.dev):");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##registryUrl", ref registryUrlInput, 512);

        if (ImGui.Button("Apply"))
        {
            if (!ClubRegistryClient.TryNormalizeRegistryUrl(registryUrlInput, out var normalized))
            {
                plugin.Notify("ClubFFXIV",
                    "Not a valid registry.",
                    NotificationType.Error,
                    durationSeconds: 8);
            }
            else if (normalized.Length == 0)
            {
                // Blank → disable, no probe needed.
                plugin.Config.RegistryUrl = "";
                plugin.Config.Save();
                plugin.RebuildRegistryClient();
                plugin.Notify("ClubFFXIV",
                    "Registry disabled (URL is blank).",
                    NotificationType.Info);
            }
            else
            {
                // Probe /health before persisting so a syntactically-valid but
                // wrong URL doesn't reach the per-tick ward fetcher.
                var candidate = normalized;
                // Flip the indicator to "checking..." on the UI thread, before
                // backgrounding — otherwise the previous "connected" lingers
                // through the entire probe and the user has no idea anything's
                // happening.
                plugin.SetRegistryChecking();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var probe = new ClubRegistryClient(candidate);
                        await probe.CheckHealthAsync();
                        registryUrlInput = candidate;
                        plugin.Config.RegistryUrl = candidate;
                        plugin.Config.Save();
                        plugin.RebuildRegistryClient();
                        plugin.SetRegistryConnected(true);
                        plugin.Notify("ClubFFXIV",
                            "Registry connected.",
                            NotificationType.Success);
                    }
                    catch (Exception ex)
                    {
                        // Underlying message goes to the log for diagnosis;
                        // the user-facing notification stays uniform.
                        Plugin.Log.Error(ex, "Registry health check failed");
                        plugin.SetRegistryConnected(false);
                        plugin.Notify("ClubFFXIV",
                            "Not a valid registry.",
                            NotificationType.Error,
                            durationSeconds: 8);
                    }
                });
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled(
            !plugin.RegistryEnabled ? "○ disabled" :
            plugin.RegistryConnected == true ? "● connected" :
            plugin.RegistryConnected == false ? "○ not connected" :
            "○ checking...");

        // Only offer Browse when the probe says the registry is actually
        // reachable. Disabled / unverified / failed states all hide the entry
        // point — there's nothing to browse if the call would just error.
        if (plugin.RegistryConnected == true)
        {
            ImGui.Spacing();
            if (ImGui.Button("Browse Public Directory"))
                plugin.ToggleDirectory();
            ImGui.SameLine();
            ImGui.TextDisabled("(or /pclub directory)");
        }

        ImGui.Spacing();
        var djId = plugin.DjId;
        if (djId == null)
        {
            ImGui.TextDisabled("DJ identity: not yet generated (created on first publish).");
        }
        else
        {
            ImGui.TextUnformatted("DJ ID:");
            ImGui.SameLine();
            ImGui.TextDisabled(djId[..16] + "...");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy"))
            {
                ImGui.SetClipboardText(djId);
                plugin.Notify("ClubFFXIV", "DJ ID copied.", NotificationType.Info);
            }
        }
    }

    /// <summary>
    /// One row in the unified My Houses list. A house can be saved-locally
    /// (offline-first), published-to-registry, or both — Calibrate already
    /// keeps both copies in sync, and Edit propagates name/description to
    /// whichever copies exist.
    /// </summary>
    private readonly record struct UnifiedHouseRow(
        string Key,
        ClubEntry? Saved,
        ClubEntry? Published)
    {
        public ClubEntry Primary => Published ?? Saved!;
        public bool IsPublished => Published != null;
        public bool HasSavedCopy => Saved != null;
        public bool IsLocalOnly => Saved != null && Published == null;
    }

    private System.Collections.Generic.IEnumerable<UnifiedHouseRow> EnumerateMyHouses()
    {
        var keys = new System.Collections.Generic.HashSet<string>(plugin.Config.SavedHouses.Keys);
        foreach (var k in plugin.Config.PublishedHouses.Keys) keys.Add(k);
        foreach (var k in keys)
        {
            plugin.Config.SavedHouses.TryGetValue(k, out var saved);
            plugin.Config.PublishedHouses.TryGetValue(k, out var pub);
            if (saved == null && pub == null) continue;
            yield return new UnifiedHouseRow(k, saved, pub);
        }
    }

    private void DrawMyHousesSection()
    {
        var rows = new System.Collections.Generic.List<UnifiedHouseRow>(EnumerateMyHouses());

        ImGui.TextUnformatted($"My Houses — {rows.Count}");
        ImGui.Spacing();

        if (rows.Count == 0)
        {
            ImGui.TextDisabled(
                "No saved or published houses yet. Stand inside your house and " +
                "use the Current Location section above to save or publish.");
            return;
        }

        DrawHousesTable(rows);
    }

    private void DrawHousesTable(System.Collections.Generic.List<UnifiedHouseRow> allRows)
    {
        var flags = ImGuiTableFlags.Resizable
                  | ImGuiTableFlags.Reorderable
                  | ImGuiTableFlags.Hideable
                  | ImGuiTableFlags.Sortable
                  | ImGuiTableFlags.RowBg
                  | ImGuiTableFlags.BordersOuter
                  | ImGuiTableFlags.BordersV
                  | ImGuiTableFlags.ScrollY
                  | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##myHousesTable", 6, flags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupColumn("Calibrated",
            ImGuiTableColumnFlags.WidthFixed, 28f, HCalib);
        ImGui.TableSetupColumn("Name",
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort,
            1.5f, HName);
        ImGui.TableSetupColumn("State",
            ImGuiTableColumnFlags.WidthFixed, 110f, HState);
        ImGui.TableSetupColumn("Description",
            ImGuiTableColumnFlags.WidthStretch, 2.0f, HDescription);
        ImGui.TableSetupColumn("Stream URL",
            ImGuiTableColumnFlags.WidthStretch, 1.8f, HUrl);
        ImGui.TableSetupColumn("Actions",
            ImGuiTableColumnFlags.WidthFixed
            | ImGuiTableColumnFlags.NoSort
            | ImGuiTableColumnFlags.NoHide,
            180f, HActions);

        ImGui.TableSetupScrollFreeze(0, 2);
        DrawHousesTableHeaders();

        // Filter row pinned with the headers. Calibrated and State columns
        // skip the filter input — they're small enumerated value sets where
        // sorting beats typing.
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); // Calibrated — no filter
        DrawHouseFilterCell("##fhName", ref houseNameFilter);
        ImGui.TableNextColumn(); // State — no filter
        DrawHouseFilterCell("##fhDesc", ref houseDescriptionFilter);
        DrawHouseFilterCell("##fhUrl", ref houseUrlFilter);
        ImGui.TableNextColumn(); // Actions — no filter

        var filtered = new System.Collections.Generic.List<UnifiedHouseRow>(allRows.Count);
        foreach (var r in allRows)
            if (HouseMatchesFilters(r)) filtered.Add(r);

        SortHouseRows(filtered);

        var canCalibrate = plugin.CurrentWard.HasValue;
        string? toDeleteSavedOnly = null;
        foreach (var row in filtered)
            DrawHouseTableRow(row, canCalibrate, ref toDeleteSavedOnly);

        ImGui.EndTable();

        if (toDeleteSavedOnly != null)
        {
            // If the form window was open editing this row, close it so the
            // user isn't left looking at a phantom edit panel.
            if (plugin.ClubFormWindow.IsOpen) plugin.ClubFormWindow.IsOpen = false;
            plugin.DeleteSavedHouse(toDeleteSavedOnly);
            plugin.Notify("ClubFFXIV", "Removed saved house.", NotificationType.Success);
        }
    }

    private static void DrawHouseFilterCell(string id, ref string value)
    {
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText(id, ref value, 64);
    }

    // Manual header row so the Calibrated column can render a FontAwesome
    // crosshair glyph (the icon font has to be pushed before TableHeader for
    // the glyph to resolve). Other columns pass through their setup names.
    private static void DrawHousesTableHeaders()
    {
        var iconFont = Plugin.PluginInterface.UiBuilder.FontIcon;
        int columnCount = ImGui.TableGetColumnCount();

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (int col = 0; col < columnCount; col++)
        {
            if (!ImGui.TableSetColumnIndex(col)) continue;
            ImGui.PushID(col);

            if (col == 0)
            {
                ImGui.PushFont(iconFont);
                ImGui.TableHeader(FontAwesomeIcon.Crosshairs.ToIconString());
                ImGui.PopFont();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Door calibration");
            }
            else
            {
                ImGui.TableHeader(ImGui.TableGetColumnName(col));
            }

            ImGui.PopID();
        }
    }

    // Compact icon button using the FontAwesome icon font. The "##id" suffix
    // gives ImGui a stable widget identity that doesn't depend on the glyph,
    // so multiple buttons with the same icon (e.g. two Pencils, or Ban for
    // both Unpublish and Delete) don't collide. AllowWhenDisabled keeps the
    // tooltip visible when the button is wrapped in BeginDisabled — important
    // because an icon-only disabled button is otherwise unreadable.
    private static bool IconSmallButton(FontAwesomeIcon icon, string id, string tooltip)
    {
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        bool clicked = ImGui.SmallButton(icon.ToIconString() + "##" + id);
        ImGui.PopFont();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private bool HouseMatchesFilters(UnifiedHouseRow row)
    {
        var entry = row.Primary;
        if (houseNameFilter.Length > 0
            && !entry.DisplayName.Contains(houseNameFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (houseDescriptionFilter.Length > 0
            && !entry.Description.Contains(houseDescriptionFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (houseUrlFilter.Length > 0
            && !entry.StreamUrl.Contains(houseUrlFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static void SortHouseRows(System.Collections.Generic.List<UnifiedHouseRow> rows)
    {
        var specs = ImGui.TableGetSortSpecs();
        uint key = HName;
        var ascending = true;
        if (specs.SpecsCount > 0)
        {
            var spec = specs.Specs;
            key = spec.ColumnUserID;
            ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
        }

        rows.Sort((a, b) =>
        {
            var ea = a.Primary;
            var eb = b.Primary;
            var cmp = key switch
            {
                HCalib => CalibratedSortKey(ea).CompareTo(CalibratedSortKey(eb)),
                HName => string.Compare(ea.DisplayName, eb.DisplayName,
                                        StringComparison.OrdinalIgnoreCase),
                HState => StateSortKey(a).CompareTo(StateSortKey(b)),
                HDescription => string.Compare(ea.Description, eb.Description,
                                               StringComparison.OrdinalIgnoreCase),
                HUrl => string.Compare(ea.StreamUrl, eb.StreamUrl,
                                       StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
            return ascending ? cmp : -cmp;
        });
    }

    private static int CalibratedSortKey(ClubEntry e) => e.DoorPosition != null ? 0 : 1;

    private static int StateSortKey(UnifiedHouseRow row)
    {
        // Order: published+listed → published+hidden → local. Keeps the rows
        // the user is most likely actively managing at the top.
        if (row.IsPublished) return row.Primary.Listed ? 0 : 1;
        return 2;
    }

    private static string StateBadge(UnifiedHouseRow row)
    {
        if (row.IsPublished) return row.Primary.Listed ? "published" : "hidden";
        return "local";
    }

    private void DrawHouseTableRow(
        UnifiedHouseRow row,
        bool canCalibrate,
        ref string? toDeleteSavedOnly)
    {
        var key = row.Key;
        var entry = row.Primary;
        var calibrated = entry.DoorPosition != null;

        ImGui.TableNextRow();
        ImGui.PushID("myhouse-" + key);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(calibrated ? "✓" : "—");
        if (calibrated && ImGui.IsItemHovered())
        {
            var p = entry.DoorPosition!;
            ImGui.SetTooltip(
                $"Door: ({p.X:F1}, {p.Y:F1}, {p.Z:F1})\n" +
                $"Ward {entry.DoorWard + 1} · territory {entry.DoorTerritoryType}");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(entry.DisplayName);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(StateBadge(row));

        ImGui.TableNextColumn();
        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            var desc = entry.Description.Length > 200
                ? entry.Description[..197] + "..."
                : entry.Description;
            ImGui.TextWrapped(desc);
        }
        else
        {
            ImGui.TextDisabled("—");
        }

        ImGui.TableNextColumn();
        if (entry.StreamUrl.Length > 0)
        {
            // Click anywhere in the URL cell copies the stream URL — same
            // pattern as the Public Directory window.
            var preview = entry.StreamUrl.Length > 60
                ? entry.StreamUrl[..57] + "..."
                : entry.StreamUrl;
            if (ImGui.Selectable(preview))
            {
                ImGui.SetClipboardText(entry.StreamUrl);
                lastCopiedHouseKey = key;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(lastCopiedHouseKey == key
                    ? "Copied"
                    : "Click to copy");
            }
        }
        else
        {
            ImGui.TextDisabled("—");
        }

        ImGui.TableNextColumn();
        DrawHouseActions(row, calibrated, canCalibrate, ref toDeleteSavedOnly);

        ImGui.PopID();
    }

    private void DrawHouseActions(
        UnifiedHouseRow row,
        bool calibrated,
        bool canCalibrate,
        ref string? toDeleteSavedOnly)
    {
        var key = row.Key;
        var entry = row.Primary;

        // Two distinct edit paths matching the Current Location flow:
        //   • Edit local — opens the local override form (any user, any plot)
        //   • Edit club  — opens the registry edit form (we hold the DJ key
        //                  for this plot, i.e. row.IsPublished)
        // Rows with both copies show both buttons; the user picks which to edit.
        if (row.HasSavedCopy && row.Saved != null)
        {
            if (IconSmallButton(FontAwesomeIcon.Pen, "edit-local", "Edit local override"))
                plugin.ClubFormWindow.OpenLocalEdit(key, row.Saved);
            ImGui.SameLine();
        }
        if (row.IsPublished && row.Published != null)
        {
            if (!plugin.RegistryEnabled) ImGui.BeginDisabled();
            if (IconSmallButton(FontAwesomeIcon.Pen, "edit-club", "Edit club listing"))
                plugin.ClubFormWindow.OpenRegistryEdit(key, row.Published);
            if (!plugin.RegistryEnabled) ImGui.EndDisabled();
            ImGui.SameLine();
        }

        if (!canCalibrate) ImGui.BeginDisabled();
        if (IconSmallButton(FontAwesomeIcon.Crosshairs, "calibrate",
                calibrated ? "Re-calibrate door position" : "Calibrate door position"))
        {
            if (plugin.CalibrateDoor(key))
                plugin.Notify("ClubFFXIV",
                    $"Calibrated door for '{entry.DisplayName}'.",
                    NotificationType.Success);
        }
        if (!canCalibrate) ImGui.EndDisabled();
        ImGui.SameLine();

        if (row.IsPublished)
        {
            // Unpublish removes the registry copy. Saved fallback (if any)
            // remains so Indoor mode still has a URL when registry is offline.
            if (IconSmallButton(FontAwesomeIcon.Ban, "unpublish", "Unpublish from registry"))
            {
                var k = key;
                var label = entry.DisplayName;
                inflightStatus = "Unpublishing...";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await plugin.UnpublishHouseAsync(k);
                        inflightStatus = "";
                        plugin.Notify("ClubFFXIV",
                            $"Unpublished '{label}'.",
                            NotificationType.Success);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error(ex, "Unpublish failed");
                        inflightStatus = "";
                        plugin.Notify("ClubFFXIV: unpublish failed",
                            ex.Message,
                            NotificationType.Error,
                            durationSeconds: 8);
                    }
                });
            }
        }
        else if (row.IsLocalOnly)
        {
            // Local-only deletes are immediate (no network).
            if (IconSmallButton(FontAwesomeIcon.Ban, "delete", "Delete saved house"))
                toDeleteSavedOnly = key;
        }
    }

    private void DrawSpatialTuningSection()
    {
        ImGui.TextUnformatted("Spatial audio tuning");
        ImGui.Separator();
        ImGui.Spacing();

        var changed = false;

        var streamDist = plugin.Config.SpatialStreamDistance;
        if (ImGui.SliderFloat("Pre-buffer distance (m)", ref streamDist, 5f, 200f, "%.0f"))
        {
            plugin.Config.SpatialStreamDistance = streamDist;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Within this distance the stream connects and pre-buffers (silently).\n" +
                "Hides the 1-3s connect delay so audio is ready when you cross the\n" +
                "audible threshold. Should be larger than Falloff distance.");

        var falloff = plugin.Config.SpatialFalloffDistance;
        if (ImGui.SliderFloat("Falloff distance (m)", ref falloff, 5f, 100f, "%.0f"))
        {
            plugin.Config.SpatialFalloffDistance = falloff;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Audible threshold — at this distance, volume = 0 but stream may still be pre-buffering.");

        var full = plugin.Config.SpatialFullVolumeDistance;
        if (ImGui.SliderFloat("Full-volume distance (m)", ref full, 0.5f, 20f, "%.1f"))
        {
            plugin.Config.SpatialFullVolumeDistance = full;
            changed = true;
        }

        var minHz = plugin.Config.SpatialMinCutoffHz;
        if (ImGui.SliderFloat("Min cutoff Hz (most muffled)", ref minHz, 100f, 2000f, "%.0f"))
        {
            plugin.Config.SpatialMinCutoffHz = minHz;
            changed = true;
        }

        var maxHz = plugin.Config.SpatialMaxCutoffHz;
        if (ImGui.SliderFloat("Max cutoff Hz (clearest)", ref maxHz, 500f, 18000f, "%.0f"))
        {
            plugin.Config.SpatialMaxCutoffHz = maxHz;
            changed = true;
        }

        if (changed) plugin.Config.Save();

        ImGui.Spacing();
        if (ImGui.Button("Reset to defaults"))
        {
            plugin.Config.ResetSpatialTuningToDefaults();
            plugin.Config.Save();
            plugin.Notify("ClubFFXIV",
                "Spatial tuning reset to defaults.",
                NotificationType.Info);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Restores all spatial audio sliders to their factory values:\n" +
                $"  Pre-buffer = {Configuration.DefaultSpatialStreamDistance:F0} m\n" +
                $"  Falloff = {Configuration.DefaultSpatialFalloffDistance:F0} m\n" +
                $"  Full volume = {Configuration.DefaultSpatialFullVolumeDistance:F1} m\n" +
                $"  Min cutoff = {Configuration.DefaultSpatialMinCutoffHz:F0} Hz\n" +
                $"  Max cutoff = {Configuration.DefaultSpatialMaxCutoffHz:F0} Hz");
    }

}
