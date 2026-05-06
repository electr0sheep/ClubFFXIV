using System;
using System.Numerics;
using System.Threading.Tasks;
using ClubFFXIV.Game;
using ClubFFXIV.Network;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string urlInput;
    private string registryUrlInput;
    private string clubNameInput = "";
    private string clubDescriptionInput = "";
    private bool clubListedInput = true;
    private string statusLine = "";

    // Inline-rename state for the Saved / Published houses lists. Holds the
    // canonical key of the row currently being edited, plus the in-flight
    // edits (name + description + listed flag). Only one row is editable at a time.
    private string? editingKey;
    private string editingName = "";
    private string editingDescription = "";
    private bool editingListed = true;

    // Public-directory browse panel state. Loaded lazily on first open.
    private string directoryStatus = "";
    private bool directoryFetchKickoffPending;

    public ConfigWindow(Plugin plugin)
        : base("ClubFFXIV##ClubFFXIVConfig", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(600, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        urlInput = plugin.Config.LastStreamUrl;
        registryUrlInput = plugin.Config.RegistryUrl;
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawHelpBar();
        DrawStreamSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawCurrentLocationSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawProximityStatusSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawRegistrySection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawSavedHousesSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawPublishedHousesSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawDirectorySection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawSpatialTuningSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawPermissionsSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawBinariesSection();
        DrawStatusFooter();
    }

    private string newAllowDomainInput = "";
    private string newBlockDomainInput = "";

    private void DrawPermissionsSection()
    {
        if (!ImGui.CollapsingHeader("Permissions (allow / block lists)"))
            return;

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
    private bool versionsRequested;

    private void DrawBinariesSection()
    {
        if (!ImGui.CollapsingHeader("External binaries (yt-dlp, ffmpeg)"))
            return;

        // Lazy-resolve versions on first display so we don't spawn processes
        // on every plugin start.
        if (!versionsRequested)
        {
            versionsRequested = true;
            _ = Task.Run(async () =>
            {
                ytDlpVersion = await plugin.Binaries.GetYtDlpVersionAsync();
                ffmpegVersion = await plugin.Binaries.GetFfmpegVersionAsync();
            });
        }

        ImGui.TextWrapped(
            "Required for Twitch / YouTube / SoundCloud playback. " +
            "Direct MP3/Icecast streams don't need these.");
        ImGui.Spacing();

        DrawBinaryRow("yt-dlp", plugin.Binaries.YtDlpInstalled, ytDlpVersion, async () =>
        {
            ytDlpVersion = "(updating...)";
            try { ytDlpVersion = await plugin.Binaries.UpdateYtDlpAsync(); statusLine = "yt-dlp updated."; }
            catch (Exception ex) { ytDlpVersion = $"(error: {ex.Message})"; statusLine = $"yt-dlp update failed: {ex.Message}"; }
        });

        DrawBinaryRow("ffmpeg", plugin.Binaries.FfmpegInstalled, ffmpegVersion, async () =>
        {
            ffmpegVersion = "(updating, ~80 MB...)";
            try { ffmpegVersion = await plugin.Binaries.UpdateFfmpegAsync(); statusLine = "ffmpeg updated."; }
            catch (Exception ex) { ffmpegVersion = $"(error: {ex.Message})"; statusLine = $"ffmpeg update failed: {ex.Message}"; }
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

    private void DrawHelpBar()
    {
        var firstRun = string.IsNullOrEmpty(plugin.Config.LastStreamUrl);

        if (firstRun)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.25f, 0.35f, 0.6f));
            ImGui.BeginChild("##firstRunBanner", new Vector2(-1, 56), true);
            ImGui.Spacing();
            ImGui.TextWrapped("First time? Click \"Getting Started\" for a 30-second walkthrough.");
            ImGui.Spacing();
            if (ImGui.Button("Getting Started"))
                plugin.ToggleHelp();
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }
        else
        {
            // Subtle help button on the right; doesn't take a full row.
            var label = "? Help";
            var btnW = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + 4;
            ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X - btnW + ImGui.GetCursorPosX());
            if (ImGui.SmallButton(label))
                plugin.ToggleHelp();
        }
    }

    private void DrawStreamSection()
    {
        ImGui.TextWrapped("Paste a Twitch / YouTube / Icecast / Shoutcast / MP3 stream URL.");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Need a URL?\n" +
                "  • Listener: ask a DJ for their Twitch / YouTube channel or stream URL\n" +
                "  • DJ: see Help → \"I want to DJ\"\n" +
                "  • Just testing: try https://www.youtube.com/watch?v=9Tzc3ybp8vA\n" +
                "    or https://ice1.somafm.com/groovesalad-128-mp3");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##url", ref urlInput, 1024);
        ImGui.Spacing();

        if (ImGui.Button("Play", new Vector2(80, 0)))
        {
            try
            {
                plugin.Config.LastStreamUrl = urlInput;
                plugin.Config.Save();
                plugin.PlayStream(urlInput);
                statusLine = $"Playing: {urlInput}";
            }
            catch (Exception ex)
            {
                statusLine = $"Error: {ex.Message}";
                Plugin.Log.Error(ex, "Play failed from ConfigWindow");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop", new Vector2(80, 0)))
        {
            plugin.StopStream();
            statusLine = "Stopped.";
        }

        ImGui.Spacing();
        var volume = plugin.Config.Volume;
        if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, "%.2f"))
        {
            plugin.Config.Volume = volume;
            plugin.Config.Save();
            plugin.SetStreamVolume(volume);
        }

        ImGui.Spacing();
        var followFocus = plugin.Config.MuteStreamWhenUnfocused;
        if (ImGui.Checkbox("Mute stream when FFXIV is not focused", ref followFocus))
        {
            plugin.Config.MuteStreamWhenUnfocused = followFocus;
            plugin.Config.Save();
        }

        var keepInSub = plugin.Config.KeepPlayingInLinkedSubterritories;
        if (ImGui.Checkbox("Keep playing in FC workshop / linked sub-rooms", ref keepInSub))
        {
            plugin.Config.KeepPlayingInLinkedSubterritories = keepInSub;
            plugin.Config.Save();
        }
    }

    private void DrawCurrentLocationSection()
    {
        ImGui.TextUnformatted("Current Location");
        ImGui.Spacing();

        var key = plugin.CurrentPlotKey;
        var ward = plugin.CurrentWard;

        if (key.HasValue)
        {
            var name = plugin.HousingDetector.GetDisplayName(key.Value);
            ImGui.TextWrapped($"Inside: {name}");

            // Ownership badge (local check, not server-verified).
            // Read the cached value — populated each framework tick by Plugin.UpdateLocationState.
            var ownership = plugin.CurrentOwnership;
            switch (ownership)
            {
                case Game.HouseOwnership.Owner:
                    ImGui.TextColored(new Vector4(0.4f, 0.85f, 0.4f, 1f), "✓ You own this plot");
                    break;
                case Game.HouseOwnership.NotOwner:
                    ImGui.TextColored(new Vector4(0.95f, 0.7f, 0.2f, 1f),
                        "⚠ You don't appear to own this plot — Publish blocked.");
                    break;
                case Game.HouseOwnership.Unknown:
                    ImGui.TextDisabled("Ownership: unknown (not blocking publish)");
                    break;
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Club name:");
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Optional. Shown to listeners who walk past or step inside your\n" +
                    "club. Leave blank to use the in-game location name.\n" +
                    "Max 80 characters. You can rename later from the Published\n" +
                    "Houses list — re-publishes signed by your DJ key.");
            ImGui.SetNextItemWidth(-1);
            // Buffer = 81 leaves room for the null terminator while letting the
            // user type up to the server's 80-char displayName cap.
            ImGui.InputText("##clubName", ref clubNameInput, 81);

            var effectiveName = EffectiveClubName(name);

            ImGui.Spacing();
            ImGui.TextUnformatted("Description:");
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Optional. Shown to listeners in the Public Directory list\n" +
                    "and in the URL permission prompt when someone first plays\n" +
                    "your stream. Max 500 characters; line breaks supported.");
            ImGui.SetNextItemWidth(-1);
            // Buffer = 501 to allow the full 500-char server cap + null terminator.
            ImGui.InputTextMultiline(
                "##clubDescription",
                ref clubDescriptionInput,
                501,
                new Vector2(-1, 70));

            ImGui.Spacing();
            ImGui.Checkbox("Show in public directory", ref clubListedInput);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Adds your club to the Public Directory browse list (visible to\n" +
                    "anyone who opens the Public Directory panel below).\n\n" +
                    "Unchecking only hides your club from this directory list.\n" +
                    "Listeners walking past your plot, anyone who knows your plot\n" +
                    "key, and the spatial-audio proximity discovery still work\n" +
                    "exactly the same.\n\n" +
                    "If you don't want your club exposed at all, simply don't\n" +
                    "publish it — local Save URL keeps it private.");

            ImGui.Spacing();
            var noUrl = string.IsNullOrWhiteSpace(urlInput);
            if (noUrl) ImGui.BeginDisabled();
            if (ImGui.Button("Save URL for this house (local)"))
            {
                plugin.SaveCurrentHouse(effectiveName, urlInput, clubDescriptionInput);
                statusLine = $"Saved locally: {effectiveName}";
            }
            ImGui.SameLine();
            var blockedByOwnership = ownership == Game.HouseOwnership.NotOwner
                                     && !plugin.Config.AllowPublishWithoutOwnership;
            var publishDisabled = !plugin.RegistryEnabled || blockedByOwnership;
            if (publishDisabled) ImGui.BeginDisabled();
            if (ImGui.Button("Publish to registry"))
            {
                var dn = effectiveName;
                var desc = clubDescriptionInput;
                var url = urlInput;
                var listed = clubListedInput;
                statusLine = "Publishing...";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await plugin.PublishCurrentHouseAsync(dn, url, desc, listed);
                        statusLine = listed
                            ? $"Published: {dn}"
                            : $"Published (hidden from directory): {dn}";
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error(ex, "Publish failed");
                        statusLine = $"Publish failed: {ex.Message}";
                    }
                });
            }
            if (publishDisabled) ImGui.EndDisabled();
            if (noUrl) ImGui.EndDisabled();

            if (blockedByOwnership)
            {
                ImGui.Spacing();
                var allow = plugin.Config.AllowPublishWithoutOwnership;
                if (ImGui.Checkbox("Allow publish without ownership check (override)", ref allow))
                {
                    plugin.Config.AllowPublishWithoutOwnership = allow;
                    plugin.Config.Save();
                }
                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Bypasses the local 'are you the owner?' check.\n" +
                        "Use only if the detection is wrong (FC edge cases,\n" +
                        "API drift). The registry's first-claim-wins rule still\n" +
                        "applies — you can't take a plot another DJ already has.");
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

    private void DrawProximityStatusSection()
    {
        ImGui.TextUnformatted($"Playback: {plugin.CurrentMode}");

        var prox = plugin.CurrentProximity;
        if (prox.HasValue)
        {
            var p = prox.Value;
            var label = p.Streaming
                ? (p.Audible ? "Approaching" : "Pre-buffering")
                : "Closest (out of range)";
            ImGui.Text($"{label}: {p.Candidate.DisplayName}");
            ImGui.Text($"Distance: {p.Distance:F1} m   Nearness: {p.NormalizedNearness * 100:F0}%");
        }
        else if (plugin.CurrentWard.HasValue)
        {
            ImGui.TextDisabled("No clubs found in this ward.");
        }
    }

    private void DrawRegistrySection()
    {
        ImGui.TextUnformatted("Registry");
        ImGui.Spacing();
        ImGui.TextWrapped("Backend URL (e.g. https://clubffxiv-registry.workers.dev):");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##registryUrl", ref registryUrlInput, 512);

        if (ImGui.Button("Apply"))
        {
            plugin.Config.RegistryUrl = registryUrlInput.Trim();
            plugin.Config.Save();
            plugin.RebuildRegistryClient();
            statusLine = plugin.RegistryEnabled
                ? "Registry connected."
                : "Registry disabled (URL is blank).";
        }
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.RegistryEnabled ? "● enabled" : "○ disabled");

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
                statusLine = "DJ ID copied.";
            }
        }
    }

    private void DrawSavedHousesSection()
    {
        ImGui.TextUnformatted($"Saved Houses (local) — {plugin.Config.SavedHouses.Count}");
        ImGui.Spacing();
        DrawHouseList(plugin.Config.SavedHouses, "saved", allowUnpublish: false);
    }

    private void DrawPublishedHousesSection()
    {
        ImGui.TextUnformatted($"Published Houses (registry) — {plugin.Config.PublishedHouses.Count}");
        ImGui.Spacing();
        if (!plugin.RegistryEnabled)
        {
            ImGui.TextDisabled("Set a registry URL above to manage published clubs.");
            return;
        }
        DrawHouseList(plugin.Config.PublishedHouses, "pub", allowUnpublish: true);
    }

    private void DrawDirectorySection()
    {
        if (!ImGui.CollapsingHeader("Public Directory (browse all listed clubs)"))
            return;

        if (!plugin.RegistryEnabled)
        {
            ImGui.TextDisabled("Set a registry URL above to browse the directory.");
            return;
        }

        // Lazy first-fetch the moment the header opens (no fetch happens
        // while the section is collapsed, since CollapsingHeader short-circuits).
        if (plugin.DirectoryCache == null
            && !plugin.DirectoryFetchInFlight
            && !directoryFetchKickoffPending)
        {
            KickoffDirectoryFetch(force: false);
        }

        if (ImGui.Button("Refresh"))
        {
            if (!plugin.DirectoryFetchInFlight && !directoryFetchKickoffPending)
                KickoffDirectoryFetch(force: true);
        }

        var cache = plugin.DirectoryCache;
        if (cache != null)
        {
            ImGui.SameLine();
            var ago = DateTime.UtcNow - plugin.DirectoryCacheFetchedAt;
            var summary = cache.Clubs.Count == 1 ? "1 club listed" : $"{cache.Clubs.Count} clubs listed";
            ImGui.TextDisabled($"({summary}, fetched {FormatAgo(ago)})");
        }

        if (!string.IsNullOrEmpty(directoryStatus))
        {
            ImGui.Spacing();
            ImGui.TextDisabled(directoryStatus);
        }

        if (cache == null) return;

        if (cache.Clubs.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No clubs are currently listed in the directory.");
            return;
        }

        ImGui.Spacing();
        ImGui.BeginChild("##directoryList", new Vector2(-1, 320), true);
        foreach (var club in cache.Clubs)
        {
            ImGui.PushID("dir-" + club.PlotKey);

            ImGui.TextUnformatted(club.DisplayName);
            ImGui.TextDisabled("  " + FormatPlotKeyLocation(club.PlotKey));

            // Description (when present): wrap inside a slightly indented region.
            // Trim cosmetically so a long entry doesn't dominate the list — full
            // text still shows on the URL permission prompt for the same club.
            if (!string.IsNullOrWhiteSpace(club.Description))
            {
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
                    urlInput = club.StreamUrl;
                    plugin.Config.LastStreamUrl = club.StreamUrl;
                    plugin.Config.Save();
                    plugin.PlayStream(club.StreamUrl, new ClubContext(club.DisplayName, club.Description));
                    statusLine = $"Playing: {club.DisplayName}";
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Directory Play failed");
                    statusLine = $"Error: {ex.Message}";
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Load URL"))
            {
                urlInput = club.StreamUrl;
                statusLine = $"Loaded URL from {club.DisplayName}";
            }

            ImGui.TextDisabled("  " + (club.StreamUrl.Length > 70
                ? club.StreamUrl[..67] + "..."
                : club.StreamUrl));

            ImGui.PopID();
            ImGui.Spacing();
        }
        ImGui.EndChild();
    }

    private void KickoffDirectoryFetch(bool force)
    {
        directoryFetchKickoffPending = true;
        directoryStatus = "Loading directory...";
        _ = Task.Run(async () =>
        {
            try
            {
                await plugin.FetchDirectoryAsync(force);
                directoryStatus = "";
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Directory fetch failed: {ex.Message}");
                directoryStatus = $"Error: {ex.Message}";
            }
            finally
            {
                directoryFetchKickoffPending = false;
            }
        });
    }

    private string FormatPlotKeyLocation(string plotKeyCanonical)
    {
        if (!PlotKey.TryParse(plotKeyCanonical, out var key))
            return plotKeyCanonical;
        return $"World {key.WorldId} — {plugin.HousingDetector.GetDisplayName(key)}";
    }

    private void DrawHouseList(System.Collections.Generic.Dictionary<string, ClubEntry> dict, string idPrefix, bool allowUnpublish)
    {
        var canCalibrate = plugin.CurrentWard.HasValue;
        string? toRemove = null;

        foreach (var (k, entry) in dict)
        {
            ImGui.PushID(idPrefix + "-" + k);

            var calibrated = entry.DoorPosition != null;
            var badge = calibrated ? "[+]" : "[ ]";

            if (editingKey == k)
            {
                ImGui.TextUnformatted(badge);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##rename", ref editingName, 81);

                ImGui.TextUnformatted("Description:");
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextMultiline(
                    "##editDescription",
                    ref editingDescription,
                    501,
                    new Vector2(-1, 60));

                if (ImGui.SmallButton("Save"))
                {
                    var newName = editingName.Trim();
                    if (string.IsNullOrEmpty(newName))
                    {
                        statusLine = "Save cancelled — name cannot be empty.";
                    }
                    else if (allowUnpublish)
                    {
                        var key = k;
                        var dn = newName;
                        var desc = editingDescription;
                        var listed = editingListed;
                        statusLine = "Saving...";
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await plugin.RenamePublishedHouseAsync(key, dn, desc, listed);
                                statusLine = listed
                                    ? $"Updated: {dn}"
                                    : $"Updated (hidden from directory): {dn}";
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.Error(ex, "Save failed");
                                statusLine = $"Save failed: {ex.Message}";
                            }
                        });
                    }
                    else
                    {
                        plugin.RenameSavedHouse(k, newName, editingDescription);
                        statusLine = $"Saved (local): {newName}";
                    }
                    editingKey = null;
                    editingName = "";
                    editingDescription = "";
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel"))
                {
                    editingKey = null;
                    editingName = "";
                    editingDescription = "";
                }

                // Listed toggle is only meaningful for Published Houses
                // (the directory is server-side; saved-only houses aren't
                // in the registry to begin with).
                if (allowUnpublish)
                {
                    ImGui.SameLine();
                    ImGui.Checkbox("Show in public directory", ref editingListed);
                    ImGui.SameLine();
                    ImGui.TextDisabled("(?)");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "Unchecking only hides this club from the Public\n" +
                            "Directory list — anyone who knows your plot key or\n" +
                            "walks past still discovers it. For full privacy,\n" +
                            "Unpublish instead.");
                }
            }
            else
            {
                var hiddenSuffix = allowUnpublish && !entry.Listed ? "  (hidden)" : "";
                ImGui.TextUnformatted($"{badge} {entry.DisplayName}{hiddenSuffix}");

                if (ImGui.SmallButton("Load"))
                {
                    urlInput = entry.StreamUrl;
                    statusLine = $"Loaded URL from {entry.DisplayName}";
                }
                ImGui.SameLine();

                if (ImGui.SmallButton(allowUnpublish ? "Edit" : "Rename"))
                {
                    editingKey = k;
                    editingName = entry.DisplayName;
                    editingDescription = entry.Description;
                    editingListed = entry.Listed;
                }
                ImGui.SameLine();

                if (!canCalibrate) ImGui.BeginDisabled();
                if (ImGui.SmallButton(calibrated ? "Re-calibrate" : "Calibrate door"))
                {
                    if (plugin.CalibrateDoor(k))
                        statusLine = $"Calibrated: {entry.DisplayName}";
                }
                if (!canCalibrate) ImGui.EndDisabled();
                ImGui.SameLine();

                if (allowUnpublish)
                {
                    if (ImGui.SmallButton("Unpublish"))
                    {
                        var key = k;
                        statusLine = "Unpublishing...";
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await plugin.UnpublishHouseAsync(key);
                                statusLine = "Unpublished.";
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.Error(ex, "Unpublish failed");
                                statusLine = $"Unpublish failed: {ex.Message}";
                            }
                        });
                    }
                }
                else
                {
                    if (ImGui.SmallButton("Delete")) toRemove = k;
                }
            }

            ImGui.TextDisabled("  " + entry.StreamUrl);
            if (calibrated)
            {
                var p = entry.DoorPosition!;
                ImGui.TextDisabled(
                    $"  door: ({p.X:F1}, {p.Y:F1}, {p.Z:F1})  ward {entry.DoorWard + 1}  territory {entry.DoorTerritoryType}");
            }
            ImGui.PopID();
        }

        if (toRemove != null)
        {
            // If the user was mid-rename on the row they just deleted, drop edit state.
            if (editingKey == toRemove)
            {
                editingKey = null;
                editingName = "";
                editingDescription = "";
            }
            plugin.DeleteSavedHouse(toRemove);
            statusLine = "Removed saved house.";
        }
    }

    /// <summary>
    /// Resolve the club name to use when saving / publishing the current house.
    /// Falls back to the in-game location name when the user leaves the input blank.
    /// </summary>
    private string EffectiveClubName(string fallback)
    {
        var trimmed = clubNameInput.Trim();
        return string.IsNullOrEmpty(trimmed) ? fallback : trimmed;
    }

    private void DrawSpatialTuningSection()
    {
        if (!ImGui.CollapsingHeader("Spatial audio tuning"))
            return;

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
            statusLine = "Spatial tuning reset to defaults.";
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

    private void DrawStatusFooter()
    {
        if (string.IsNullOrEmpty(statusLine)) return;
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(statusLine);
    }
}
