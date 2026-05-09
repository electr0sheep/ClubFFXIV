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
            if (ImGui.BeginTabItem("Permissions"))
            {
                DrawPermissionsSection();
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
        // The row is *always* reserved (separator + frame-height row) so the
        // tab body doesn't reflow each time a network call kicks off; the
        // text just appears in the pre-allocated space. Final results go to
        // Dalamud notifications, not here.
        ImGui.Spacing();
        ImGui.Separator();
        if (!string.IsNullOrEmpty(inflightStatus))
            ImGui.TextDisabled(inflightStatus);
        else
            ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight()));
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
        // Playback is small (a handful of toggles) — render inline as the
        // landing content so the tab isn't a wall of collapsed sections on
        // open. Spatial tuning is the heavy section and is collapsible
        // (default-open) so users who don't tweak it can fold it away.
        DrawPlaybackSection();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Spatial audio tuning",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawSpatialTuningSection();
        }
    }

    private void DrawPlaybackSection()
    {
        UiHelpers.SectionHeader("Playback");

        // Loop + Random playlist order live as icon toggles in the music
        // player itself (next to Stop) — they're playback-mode controls, so
        // they belong with the player chrome rather than buried in Settings.

        var showThumbs = plugin.Config.ShowNowPlayingThumbnails;
        if (ImGui.Checkbox("Show thumbnails in Now Playing", ref showThumbs))
        {
            plugin.Config.ShowNowPlayingThumbnails = showThumbs;
            plugin.Config.Save();
        }
        UiHelpers.HelpMarker(
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

        var showProx = plugin.Config.ShowPlaybackModeAndProximity;
        if (ImGui.Checkbox("Show playback mode and proximity details", ref showProx))
        {
            plugin.Config.ShowPlaybackModeAndProximity = showProx;
            plugin.Config.Save();
        }
        UiHelpers.HelpMarker(
            "Show the diagnostic readout at the bottom of the music player:\n" +
            "  • Playback mode (Spatial outdoor / Indoor solo / etc.)\n" +
            "  • Closest club + distance in yalms\n" +
            "  • L/R balance and front/back fade bars\n" +
            "Off (default) = player shows only Now Playing + transport controls.");
    }

    private void DrawAdvancedTab()
    {
        // Binaries is the most-likely-to-need section in Advanced (yt-dlp
        // updates, ffmpeg installs, and the cookies-from-browser auth knob
        // all live there). Multi-stream follows. Permissions used to live
        // here too but earned its own top-level tab — trust/safety is a
        // distinct concern from install state and multi-voice mixing, and
        // users hunting for "why did this domain get blocked" scan tab
        // labels rather than expanding collapsed sections.
        if (ImGui.CollapsingHeader("External binaries (yt-dlp, ffmpeg, deno)",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBinariesSection();
            ImGui.Spacing();
            DrawYtDlpSection();
        }
        if (ImGui.CollapsingHeader("Multi-stream"))
        {
            DrawMultiStreamSection();
        }
    }

    private void DrawYtDlpSection()
    {
        // Sub-section header inside the Binaries CollapsingHeader — yt-dlp's
        // browser-cookie auth setting belongs alongside the install/update
        // controls, but visually wants its own subtitle.
        UiHelpers.SectionHeader("yt-dlp authentication");

        ImGui.TextWrapped(
            "If YouTube starts showing \"Sign in to confirm you're not a bot\" errors, " +
            "set this to \"firefox\". Make sure you're logged into YouTube in Firefox " +
            "first — yt-dlp will read your session cookies from there. Leave empty to " +
            "stay anonymous.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Cookies from browser:");
        UiHelpers.HelpMarker(
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
        // Header is the CollapsingHeader in DrawAdvancedTab.

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
        UiHelpers.SectionHeader("Permissions");

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
            if (IconSmallButton(FontAwesomeIcon.Trash, idPrefix + "-rm-" + item, "Remove")) toRemove = item;
            ImGui.PopID();
        }
        if (toRemove != null) { set.Remove(toRemove); plugin.Config.Save(); }

        ImGui.SetNextItemWidth(280);
        ImGui.InputText($"##{idPrefix}-add", ref input, 256);
        ImGui.SameLine();
        if (IconSmallButton(FontAwesomeIcon.Plus, idPrefix + "-addbtn", "Add") && !string.IsNullOrWhiteSpace(input))
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
            if (IconSmallButton(FontAwesomeIcon.Trash, idPrefix + "-rm-" + item, "Remove")) toRemove = item;
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
        // Header is the CollapsingHeader in DrawAdvancedTab.

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
        // Coloured status glyph keeps install state scannable across the
        // three rows; the SetupWizard uses the same colours so the visual
        // language matches.
        var color = installed ? UiColors.Success : UiColors.Pending;
        var glyph = installed ? "✓" : "—";
        ImGui.TextColored(color, $"{glyph} {name}:");
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
        UiHelpers.SectionHeader("Registry");
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
        if (!plugin.RegistryEnabled)
            UiHelpers.StatusDot(UiHelpers.Status.Disabled, "disabled");
        else if (plugin.RegistryConnected == true)
            UiHelpers.StatusDot(UiHelpers.Status.Ok, "connected");
        else if (plugin.RegistryConnected == false)
            UiHelpers.StatusDot(UiHelpers.Status.Bad, "not connected");
        else
            UiHelpers.StatusDot(UiHelpers.Status.Pending, "checking...");

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
            if (IconSmallButton(FontAwesomeIcon.Copy, "dj-id-copy", "Copy"))
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

    private void DrawCurrentLocationSection()
    {
        UiHelpers.SectionHeader("Current Location");

        var key = plugin.CurrentPlotKey;
        var ward = plugin.CurrentWard;

        if (key.HasValue)
        {
            var plot = key.Value;
            var canonical = plot.Canonical;
            var name = plugin.HousingDetector.GetDisplayName(plot);
            ImGui.TextWrapped($"Inside: {name}");

            // Ownership badge (local check, not server-verified). Read the
            // cached value — populated each framework tick by UpdateLocationState
            // so off-thread callers don't access ObjectTable.
            var ownership = plugin.CurrentOwnership;
            switch (ownership)
            {
                case Game.HouseOwnership.Owner:
                    ImGui.TextColored(UiColors.Success, "✓ You own this plot");
                    break;
                case Game.HouseOwnership.NotOwner:
                    ImGui.TextColored(UiColors.Warning,
                        "⚠ You don't appear to own this plot");
                    break;
                case Game.HouseOwnership.Unknown:
                    ImGui.TextDisabled("Ownership: unknown");
                    break;
            }

            ImGui.Spacing();

            // --- Path 1: Local override (any user, any plot) -----------------
            var hasLocal = plugin.Config.SavedHouses.TryGetValue(canonical, out var savedEntry);
            if (hasLocal && savedEntry != null)
            {
                if (ImGui.Button("Edit local override"))
                    plugin.ClubFormWindow.OpenLocalEdit(canonical, savedEntry);
            }
            else
            {
                if (ImGui.Button("Create local override"))
                    plugin.ClubFormWindow.OpenLocalCreate(plot);
            }
            UiHelpers.HelpMarker(
                "A local override is a private URL stored on your client only.\n" +
                "When you walk into this plot, your override plays — even if the\n" +
                "registry has a different URL for the plot. Visible to no one\n" +
                "but you.");

            // --- Path 2: Registry (publish if owner & new; edit if has key) -
            var hasPublishedKey = plugin.Config.PublishedHouses.TryGetValue(canonical, out var publishedEntry);
            var blockedByOwnership = ownership == Game.HouseOwnership.NotOwner;

            ImGui.SameLine();
            if (hasPublishedKey && publishedEntry != null)
            {
                if (!plugin.RegistryEnabled) ImGui.BeginDisabled();
                if (ImGui.Button("Edit club"))
                    plugin.ClubFormWindow.OpenRegistryEdit(canonical, publishedEntry);
                if (!plugin.RegistryEnabled) ImGui.EndDisabled();
            }
            else
            {
                var publishDisabled = !plugin.RegistryEnabled || blockedByOwnership;
                if (publishDisabled) ImGui.BeginDisabled();
                if (ImGui.Button("Publish new club"))
                    plugin.ClubFormWindow.OpenRegistryPublish(plot);
                if (publishDisabled) ImGui.EndDisabled();
            }
        }
        else if (ward.HasValue)
        {
            var districtName = plugin.HousingDetector.LookupDistrictName(ward.Value.TerritoryType);
            ImGui.TextWrapped($"Roaming: {districtName} Ward {ward.Value.Ward + 1}");
            ImGui.TextDisabled("Walk to a house's door and use the Calibrate button on a saved/published entry below.");
        }
        else
        {
            ImGui.TextDisabled("Not in housing.");
        }
    }

    private void DrawMyHousesSection()
    {
        var rows = new System.Collections.Generic.List<UnifiedHouseRow>(EnumerateMyHouses());

        UiHelpers.SectionHeader($"My Houses — {rows.Count}");

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
        //   • Pen   — local override form (any user, any plot)
        //   • Globe — registry edit form (we hold the DJ key for this plot,
        //             i.e. row.IsPublished). Globe instead of a second Pen
        //             so a row that shows both buttons is visually
        //             unambiguous; the registry copy is the "globe-y" one.
        if (row.HasSavedCopy && row.Saved != null)
        {
            if (IconSmallButton(FontAwesomeIcon.Pen, "edit-local", "Edit local override"))
                plugin.ClubFormWindow.OpenLocalEdit(key, row.Saved);
            ImGui.SameLine();
        }
        if (row.IsPublished && row.Published != null)
        {
            if (!plugin.RegistryEnabled) ImGui.BeginDisabled();
            if (IconSmallButton(FontAwesomeIcon.Globe, "edit-club", "Edit club listing"))
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
        // Header is the CollapsingHeader in DrawSettingsTab — no internal
        // title needed here.
        var changed = false;

        var directional = plugin.Config.SpatialDirectionalAudio;
        if (ImGui.Checkbox("Directional audio (L/R pan)", ref directional))
        {
            plugin.Config.SpatialDirectionalAudio = directional;
            changed = true;
        }
        UiHelpers.HelpMarker(
            "Pans each club's audio left/right based on where its door sits\n" +
            "relative to the camera. Off = every voice plays centered.");

        if (directional)
        {
            ImGui.Indent();

            var panStrength = plugin.Config.SpatialPanStrength;
            if (ImGui.SliderFloat("Pan strength", ref panStrength, 0f, 1f, "%.2f"))
            {
                plugin.Config.SpatialPanStrength = panStrength;
                changed = true;
            }
            UiHelpers.HelpMarker(
                "How dramatically audio pans toward each side. 1 = ping-pong\n" +
                "stereo (far ear silent at full pan); 0 = no panning at all.\n" +
                "Default 0.7 leaves about 30% of the source audible in the far\n" +
                "ear at the extremes, which roughly matches how real ears\n" +
                "actually localize sounds at 90°.");

            var rearMuffle = plugin.Config.SpatialRearMuffleStrength;
            if (ImGui.SliderFloat("Rear-muffle strength", ref rearMuffle, 0f, 1f, "%.2f"))
            {
                plugin.Config.SpatialRearMuffleStrength = rearMuffle;
                changed = true;
            }
            UiHelpers.HelpMarker(
                "How much extra muffle (lowpass) doors pick up when they're\n" +
                "directly behind you — a subtle front/back cue. 0 disables it,\n" +
                "1 halves the cutoff at full rear.");

            var invert = plugin.Config.SpatialInvertPan;
            if (ImGui.Checkbox("Invert L/R", ref invert))
            {
                plugin.Config.SpatialInvertPan = invert;
                changed = true;
            }
            UiHelpers.HelpMarker(
                "Flip the pan sign if doors on your left sound like they're\n" +
                "on your right. Should not normally be needed.");

            ImGui.Unindent();
        }

        ImGui.Spacing();

        // Distance sliders. The three values must satisfy
        // pre-buffer ≥ falloff ≥ full-volume; if a user drags them out of
        // order the section below renders an inline warning rather than
        // silently snapping the values.
        var streamDist = plugin.Config.SpatialStreamDistance;
        if (ImGui.SliderFloat("Pre-buffer distance (yalms)", ref streamDist, 5f, 200f, "%.0f"))
        {
            plugin.Config.SpatialStreamDistance = streamDist;
            changed = true;
        }
        UiHelpers.HelpMarker(
            "Within this distance the stream connects and pre-buffers (silently).\n" +
            "Hides the 1-3s connect delay so audio is ready when you cross the\n" +
            "audible threshold. Should be larger than Falloff distance.");

        var falloff = plugin.Config.SpatialFalloffDistance;
        if (ImGui.SliderFloat("Falloff distance (yalms)", ref falloff, 5f, 100f, "%.0f"))
        {
            plugin.Config.SpatialFalloffDistance = falloff;
            changed = true;
        }
        UiHelpers.HelpMarker("Audible threshold — at this distance, volume = 0 but stream may still be pre-buffering.");

        var full = plugin.Config.SpatialFullVolumeDistance;
        if (ImGui.SliderFloat("Full-volume distance (yalms)", ref full, 0.5f, 20f, "%.1f"))
        {
            plugin.Config.SpatialFullVolumeDistance = full;
            changed = true;
        }
        UiHelpers.HelpMarker(
            "Inside this distance the stream plays at full volume — the\n" +
            "audio fades from 0 (at Falloff) to full as you approach.");

        if (streamDist < falloff)
            ImGui.TextColored(UiColors.Warning,
                "  ⚠ Pre-buffer distance is shorter than Falloff distance — streams will\n" +
                "    start mid-fade-in instead of pre-buffering silently.");
        if (falloff < full)
            ImGui.TextColored(UiColors.Warning,
                "  ⚠ Falloff distance is shorter than Full-volume distance — audio will\n" +
                "    never actually fade.");

        ImGui.Spacing();

        var minHz = plugin.Config.SpatialMinCutoffHz;
        if (ImGui.SliderFloat("Min cutoff Hz (most muffled)", ref minHz, 100f, 2000f, "%.0f"))
        {
            plugin.Config.SpatialMinCutoffHz = minHz;
            changed = true;
        }
        UiHelpers.HelpMarker(
            "Lowpass cutoff applied when a club is at the far edge of the\n" +
            "audible range — lower = more muffled, like the music is behind\n" +
            "a wall. Must stay below Max cutoff Hz.");

        var maxHz = plugin.Config.SpatialMaxCutoffHz;
        if (ImGui.SliderFloat("Max cutoff Hz (clearest)", ref maxHz, 500f, 18000f, "%.0f"))
        {
            plugin.Config.SpatialMaxCutoffHz = maxHz;
            changed = true;
        }
        UiHelpers.HelpMarker(
            "Lowpass cutoff at the closest distance — higher = clearer.\n" +
            "Once you're inside the house the lowpass disappears entirely\n" +
            "(20 kHz bypass) regardless of this value.");

        if (minHz >= maxHz)
            ImGui.TextColored(UiColors.Warning,
                "  ⚠ Min cutoff is at or above Max cutoff — the lowpass will be inverted\n" +
                "    or have no effect.");

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
        UiHelpers.HelpMarker(
            "Restores all spatial audio sliders to their factory values:\n" +
            $"  Directional = {(Configuration.DefaultSpatialDirectionalAudio ? "on" : "off")}\n" +
            $"  Pan strength = {Configuration.DefaultSpatialPanStrength:F2}\n" +
            $"  Rear muffle = {Configuration.DefaultSpatialRearMuffleStrength:F2}\n" +
            $"  Invert L/R = {(Configuration.DefaultSpatialInvertPan ? "on" : "off")}\n" +
            $"  Pre-buffer = {Configuration.DefaultSpatialStreamDistance:F0} m\n" +
            $"  Falloff = {Configuration.DefaultSpatialFalloffDistance:F0} m\n" +
            $"  Full volume = {Configuration.DefaultSpatialFullVolumeDistance:F1} m\n" +
            $"  Min cutoff = {Configuration.DefaultSpatialMinCutoffHz:F0} Hz\n" +
            $"  Max cutoff = {Configuration.DefaultSpatialMaxCutoffHz:F0} Hz");
    }

}
