using System;
using System.Numerics;
using System.Threading.Tasks;
using ClubFFXIV.Game;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string urlInput;
    private string registryUrlInput;
    private string statusLine = "";

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
        DrawSpatialTuningSection();
        DrawStatusFooter();
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
        ImGui.TextWrapped("Paste an Icecast / Shoutcast / MP3 stream URL.");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Need a URL?\n" +
                "  • Listener: ask a DJ for theirs\n" +
                "  • DJ: see Help → \"I want to DJ\"\n" +
                "  • Just testing: try https://ice1.somafm.com/groovesalad-128-mp3");
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
        var muteBgm = plugin.Config.MuteGameBgmWhilePlaying;
        if (ImGui.Checkbox("Mute FFXIV BGM while stream plays", ref muteBgm))
        {
            plugin.Config.MuteGameBgmWhilePlaying = muteBgm;
            plugin.Config.Save();
        }

        var followFocus = plugin.Config.MuteStreamWhenUnfocused;
        if (ImGui.Checkbox("Mute stream when FFXIV is not focused", ref followFocus))
        {
            plugin.Config.MuteStreamWhenUnfocused = followFocus;
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
            var noUrl = string.IsNullOrWhiteSpace(urlInput);
            if (noUrl) ImGui.BeginDisabled();
            if (ImGui.Button("Save URL for this house (local)"))
            {
                plugin.SaveCurrentHouse(name, urlInput);
                statusLine = $"Saved locally: {name}";
            }
            ImGui.SameLine();
            var blockedByOwnership = ownership == Game.HouseOwnership.NotOwner
                                     && !plugin.Config.AllowPublishWithoutOwnership;
            var publishDisabled = !plugin.RegistryEnabled || blockedByOwnership;
            if (publishDisabled) ImGui.BeginDisabled();
            if (ImGui.Button("Publish to registry"))
            {
                var dn = name;
                var url = urlInput;
                statusLine = "Publishing...";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await plugin.PublishCurrentHouseAsync(dn, url);
                        statusLine = $"Published: {dn}";
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
            var label = p.InRange ? "Approaching" : "Closest (out of range)";
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
        var autoQuery = plugin.Config.AutoQueryRegistry;
        if (ImGui.Checkbox("Auto-discover clubs on house entry", ref autoQuery))
        {
            plugin.Config.AutoQueryRegistry = autoQuery;
            plugin.Config.Save();
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

    private void DrawHouseList(System.Collections.Generic.Dictionary<string, ClubEntry> dict, string idPrefix, bool allowUnpublish)
    {
        var canCalibrate = plugin.CurrentWard.HasValue;
        string? toRemove = null;

        foreach (var (k, entry) in dict)
        {
            ImGui.PushID(idPrefix + "-" + k);

            var calibrated = entry.DoorPosition != null;
            var badge = calibrated ? "[+]" : "[ ]";
            ImGui.TextUnformatted($"{badge} {entry.DisplayName}");

            if (ImGui.SmallButton("Load"))
            {
                urlInput = entry.StreamUrl;
                statusLine = $"Loaded URL from {entry.DisplayName}";
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
            plugin.DeleteSavedHouse(toRemove);
            statusLine = "Removed saved house.";
        }
    }

    private void DrawSpatialTuningSection()
    {
        if (!ImGui.CollapsingHeader("Spatial audio tuning"))
            return;

        var changed = false;

        var falloff = plugin.Config.SpatialFalloffDistance;
        if (ImGui.SliderFloat("Falloff distance (m)", ref falloff, 5f, 100f, "%.0f"))
        {
            plugin.Config.SpatialFalloffDistance = falloff;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Beyond this distance, the stream is silent and disconnected.");

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
        if (ImGui.SliderFloat("Max cutoff Hz (clearest)", ref maxHz, 2000f, 18000f, "%.0f"))
        {
            plugin.Config.SpatialMaxCutoffHz = maxHz;
            changed = true;
        }

        if (changed) plugin.Config.Save();
    }

    private void DrawStatusFooter()
    {
        if (string.IsNullOrEmpty(statusLine)) return;
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(statusLine);
    }
}
