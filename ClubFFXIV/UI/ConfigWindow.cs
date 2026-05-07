using System;
using System.Numerics;
using System.Threading.Tasks;
using ClubFFXIV.Game;
using ClubFFXIV.Network;
using Dalamud.Interface.ImGuiNotification;
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

    // Stream URL for the My Clubs tab. Independent from urlInput (Now Playing)
    // so saving/publishing a club doesn't accidentally use whatever stream the
    // user happens to be auditioning. Pre-filled from the active plot's
    // saved/published record when the player enters a new plot.
    private string clubUrlInput = "";
    private string? lastClubUrlPlotKey;

    // Inline-progress text for in-flight async operations ("Publishing...",
    // "Saving...", "Unpublishing..."). Final results go to Dalamud notifications;
    // this field only holds the during-the-network-call indicator so the user
    // sees something between click and toast.
    private string inflightStatus = "";

    // Inline-rename state for the unified houses list. Holds the canonical key
    // of the row currently being edited, plus the in-flight edits (name +
    // description + listed flag). Only one row is editable at a time.
    private string? editingKey;
    private string editingName = "";
    private string editingDescription = "";
    private bool editingListed = true;

    // Filter input for the My Clubs houses list. Substring-matches against the
    // entry's display name, case-insensitive.
    private string houseFilterInput = "";

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
        DrawNowPlayingHeader();
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##mainTabs"))
        {
            if (ImGui.BeginTabItem("Now Playing"))
            {
                DrawNowPlayingTab();
                ImGui.EndTabItem();
            }
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

    private void DrawNowPlayingTab()
    {
        DrawStreamSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawProximityStatusSection();
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
        DrawSpatialTuningSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawPermissionsSection();
    }

    private void DrawAdvancedTab()
    {
        DrawBinariesSection();
#if DEBUG
        ImGui.Spacing();
        DrawDebugMultiStreamSection();
#endif
    }

#if DEBUG
    private void DrawDebugMultiStreamSection()
    {
        ImGui.TextUnformatted("Multi-stream (DEBUG only)");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Experimental: outdoor proximity keeps multiple nearby clubs streaming " +
            "at once, mixed by per-voice distance. Each yt-dlp source costs significant " +
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
        if (ImGui.SliderInt("Max concurrent voices", ref cap, 1, 5))
        {
            plugin.Config.MaxConcurrentStreams = Math.Clamp(cap, 1, 5);
            plugin.Config.Save();
        }
    }
#endif

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
    private bool versionsRequested;

    private void DrawBinariesSection()
    {
        ImGui.TextUnformatted("External binaries (yt-dlp, ffmpeg)");
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
            });
        }

        ImGui.TextWrapped(
            "Required for Twitch / YouTube / SoundCloud playback. " +
            "Direct MP3/Icecast streams don't need these.");
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

    /// <summary>
    /// Always-visible status strip above the tab bar: tells the user at a
    /// glance what's playing, where they are, and the current playback mode.
    /// Survives tab switches so the user can change settings without losing
    /// situational awareness.
    /// </summary>
    private void DrawNowPlayingHeader()
    {
        var mode = plugin.CurrentMode;
        var playing = mode != PlaybackMode.Off;

        var icon = playing ? "▶" : "⏸";
        var iconColor = playing
            ? new Vector4(0.4f, 0.85f, 0.4f, 1f)
            : new Vector4(0.6f, 0.6f, 0.6f, 1f);

        // Resolve a friendly label for the current stream URL, falling back
        // to "(none)" when nothing is playing or the URL is unknown.
        var url = plugin.CurrentStreamUrl;
        string label;
        if (!playing || string.IsNullOrEmpty(url))
        {
            label = "Not playing";
        }
        else
        {
            var ctx = plugin.LookupClubContextForUrl(url);
            label = !string.IsNullOrWhiteSpace(ctx?.ClubName)
                ? ctx.Value.ClubName
                : (url.Length > 60 ? url[..57] + "..." : url);
        }

        // Location segment (where the player is right now, not where the
        // stream's source is).
        string location;
        if (plugin.CurrentPlotKey is { } pk)
        {
            location = plugin.HousingDetector.GetDisplayName(pk);
        }
        else if (plugin.CurrentWard is { } ward)
        {
            var district = plugin.HousingDetector.LookupDistrictName(ward.TerritoryType);
            location = $"{district} W{ward.Ward + 1}";
        }
        else
        {
            location = "outside housing";
        }

        var modeText = playing ? mode.ToString() : "stopped";
        var volumeText = $"vol {(int)(plugin.Config.Volume * 100)}%";

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.10f, 0.12f, 1f));
        ImGui.BeginChild("##nowPlayingHeader", new Vector2(-1, 32), true);
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(iconColor, icon);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.TextDisabled($"  ·  {location}  ·  {modeText}  ·  {volumeText}");
        ImGui.EndChild();
        ImGui.PopStyleColor();
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
                // Now Playing header + chat ("Playing: ...") already cover the
                // visual feedback for a successful start.
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Play failed from ConfigWindow");
                plugin.Notify("ClubFFXIV: play failed",
                    ex.Message, NotificationType.Error, durationSeconds: 8);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop", new Vector2(80, 0)))
        {
            plugin.StopStream();
            // Now Playing header reflects "stopped" — no extra status needed.
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

        // Pre-fill the club Stream URL field when the player enters a new plot
        // that has a saved/published record. Lets the DJ re-publish without
        // re-pasting their URL. Switching plots clobbers any unsaved input —
        // acceptable trade-off for the much more common edit-existing flow.
        var canonical = key?.Canonical;
        if (canonical != lastClubUrlPlotKey)
        {
            lastClubUrlPlotKey = canonical;
            clubUrlInput = LookupSavedStreamUrl(canonical) ?? "";
        }

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
            ImGui.TextUnformatted("Stream URL:");
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "The audio stream URL listeners hear when they tune into\n" +
                    "this club. Independent of the Now Playing tab — set this\n" +
                    "once, save or publish, done. Pre-fills from the saved\n" +
                    "record when you enter a plot you've already configured.");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##clubStreamUrl", ref clubUrlInput, 1024);

            ImGui.Spacing();
            ImGui.Checkbox("Show in public directory", ref clubListedInput);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Adds your club to the Public Directory browse list (visible to\n" +
                    "anyone who opens the Public Directory from the Registry tab).\n\n" +
                    "Unchecking only hides your club from this directory list.\n" +
                    "Listeners walking past your plot, anyone who knows your plot\n" +
                    "key, and the spatial-audio proximity discovery still work\n" +
                    "exactly the same.\n\n" +
                    "If you don't want your club exposed at all, simply don't\n" +
                    "publish it — local Save URL keeps it private.");

            ImGui.Spacing();
            var noUrl = string.IsNullOrWhiteSpace(clubUrlInput);
            if (noUrl) ImGui.BeginDisabled();
            if (ImGui.Button("Save URL for this house (local)"))
            {
                plugin.SaveCurrentHouse(effectiveName, clubUrlInput, clubDescriptionInput);
                plugin.Notify("ClubFFXIV",
                    $"Saved '{effectiveName}' locally.",
                    NotificationType.Success);
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
                var url = clubUrlInput;
                var listed = clubListedInput;
                inflightStatus = "Publishing...";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await plugin.PublishCurrentHouseAsync(dn, url, desc, listed);
                        inflightStatus = "";
                        plugin.Notify("ClubFFXIV",
                            listed
                                ? $"Published '{dn}'."
                                : $"Published '{dn}' (hidden from directory).",
                            NotificationType.Success);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error(ex, "Publish failed");
                        inflightStatus = "";
                        plugin.Notify("ClubFFXIV: publish failed",
                            ex.Message,
                            NotificationType.Error,
                            durationSeconds: 8);
                    }
                });
            }
            if (publishDisabled) ImGui.EndDisabled();
            if (noUrl) ImGui.EndDisabled();

#if DEBUG
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
                        "DEBUG builds only — Release ignores this flag.\n" +
                        "The registry's first-claim-wins rule still applies —\n" +
                        "you can't take a plot another DJ already has.");
            }
#endif
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
        rows.Sort((a, b) => string.Compare(a.Primary.DisplayName, b.Primary.DisplayName,
            StringComparison.OrdinalIgnoreCase));

        ImGui.TextUnformatted($"My Houses — {rows.Count}");
        ImGui.Spacing();

        // Filter input — substring on display name, case-insensitive.
        ImGui.TextUnformatted("Filter:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-80);
        ImGui.InputText("##houseFilter", ref houseFilterInput, 128);
        ImGui.SameLine();
        if (ImGui.Button("Clear", new Vector2(70, 0)))
            houseFilterInput = "";
        ImGui.Spacing();

        if (rows.Count == 0)
        {
            ImGui.TextDisabled(
                "No saved or published houses yet. Stand inside your house and " +
                "use the Current Location section above to save or publish.");
            return;
        }

        var filter = houseFilterInput.Trim();
        var hasFilter = filter.Length > 0;
        var filterLower = hasFilter ? filter.ToLowerInvariant() : "";

        var canCalibrate = plugin.CurrentWard.HasValue;
        string? toDeleteSavedOnly = null;
        var matchCount = 0;

        foreach (var row in rows)
        {
            if (hasFilter
                && !row.Primary.DisplayName.ToLowerInvariant().Contains(filterLower))
                continue;
            matchCount++;
            DrawHouseRow(row, canCalibrate, ref toDeleteSavedOnly);
        }

        if (hasFilter && matchCount == 0)
            ImGui.TextDisabled($"No houses matched \"{filter}\".");

        if (toDeleteSavedOnly != null)
        {
            // Cancel any active inline edit on the row we just deleted.
            if (editingKey == toDeleteSavedOnly)
            {
                editingKey = null;
                editingName = "";
                editingDescription = "";
            }
            plugin.DeleteSavedHouse(toDeleteSavedOnly);
            plugin.Notify("ClubFFXIV", "Removed saved house.", NotificationType.Success);
        }
    }

    private void DrawHouseRow(
        UnifiedHouseRow row,
        bool canCalibrate,
        ref string? toDeleteSavedOnly)
    {
        var key = row.Key;
        var entry = row.Primary;
        var calibrated = entry.DoorPosition != null;
        ImGui.PushID("myhouses-" + key);

        if (editingKey == key)
            DrawHouseRowEditing(row);
        else
            DrawHouseRowReadonly(row, calibrated, canCalibrate, ref toDeleteSavedOnly);

        ImGui.TextDisabled("  " + entry.StreamUrl);
        if (calibrated)
        {
            var p = entry.DoorPosition!;
            ImGui.TextDisabled(
                $"  door: ({p.X:F1}, {p.Y:F1}, {p.Z:F1})  ward {entry.DoorWard + 1}  territory {entry.DoorTerritoryType}");
        }
        ImGui.PopID();
    }

    private void DrawHouseRowReadonly(
        UnifiedHouseRow row,
        bool calibrated,
        bool canCalibrate,
        ref string? toDeleteSavedOnly)
    {
        var key = row.Key;
        var entry = row.Primary;
        var calBadge = calibrated ? "[+]" : "[ ]";

        // Subtle status badge ("local" / "published" / "published, hidden").
        // The published-vs-local distinction governs what actions appear.
        string stateBadge;
        if (row.IsPublished)
            stateBadge = entry.Listed ? "published" : "published, hidden";
        else
            stateBadge = "local";

        ImGui.TextUnformatted($"{calBadge} {entry.DisplayName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"· {stateBadge}");

        if (ImGui.SmallButton("Load"))
            urlInput = entry.StreamUrl;
        ImGui.SameLine();

        if (ImGui.SmallButton(row.IsPublished ? "Edit" : "Rename"))
        {
            editingKey = key;
            editingName = entry.DisplayName;
            editingDescription = entry.Description;
            editingListed = entry.Listed;
        }
        ImGui.SameLine();

        if (!canCalibrate) ImGui.BeginDisabled();
        if (ImGui.SmallButton(calibrated ? "Re-calibrate" : "Calibrate door"))
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
            if (ImGui.SmallButton("Unpublish"))
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
            // Local-only deletes are immediate (no network) — Delete is the
            // only way to remove a saved-only entry.
            if (ImGui.SmallButton("Delete")) toDeleteSavedOnly = key;
        }
    }

    private void DrawHouseRowEditing(UnifiedHouseRow row)
    {
        var entry = row.Primary;
        var calBadge = entry.DoorPosition != null ? "[+]" : "[ ]";

        ImGui.TextUnformatted(calBadge);
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

        var key = row.Key;

        if (ImGui.SmallButton("Save"))
        {
            var newName = editingName.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                plugin.Notify("ClubFFXIV",
                    "Name cannot be empty — edit cancelled.",
                    NotificationType.Warning);
            }
            else if (row.IsPublished)
            {
                // Published copy: signed re-publish (network) updates the
                // registry record AND the local PublishedHouses entry. If a
                // saved copy also exists for this plot, sync its name/desc
                // afterwards so the two local mirrors stay coherent.
                var dn = newName;
                var desc = editingDescription;
                var listed = editingListed;
                var hasSaved = row.HasSavedCopy;
                inflightStatus = "Saving...";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await plugin.RenamePublishedHouseAsync(key, dn, desc, listed);
                        if (hasSaved) plugin.RenameSavedHouse(key, dn, desc);
                        inflightStatus = "";
                        plugin.Notify("ClubFFXIV",
                            listed
                                ? $"Updated '{dn}'."
                                : $"Updated '{dn}' (hidden from directory).",
                            NotificationType.Success);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error(ex, "Save failed");
                        inflightStatus = "";
                        plugin.Notify("ClubFFXIV: save failed",
                            ex.Message,
                            NotificationType.Error,
                            durationSeconds: 8);
                    }
                });
            }
            else
            {
                plugin.RenameSavedHouse(key, newName, editingDescription);
                plugin.Notify("ClubFFXIV",
                    $"Saved '{newName}' locally.",
                    NotificationType.Success);
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

        // Listed toggle is only meaningful for published houses (the directory
        // is server-side; saved-only entries aren't in the registry).
        if (row.IsPublished)
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

    /// <summary>
    /// Resolve the club name to use when saving / publishing the current house.
    /// Falls back to the in-game location name when the user leaves the input blank.
    /// </summary>
    private string EffectiveClubName(string fallback)
    {
        var trimmed = clubNameInput.Trim();
        return string.IsNullOrEmpty(trimmed) ? fallback : trimmed;
    }

    private string? LookupSavedStreamUrl(string? canonical)
    {
        if (canonical == null) return null;
        if (plugin.Config.PublishedHouses.TryGetValue(canonical, out var pub)) return pub.StreamUrl;
        if (plugin.Config.SavedHouses.TryGetValue(canonical, out var saved)) return saved.StreamUrl;
        return null;
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
