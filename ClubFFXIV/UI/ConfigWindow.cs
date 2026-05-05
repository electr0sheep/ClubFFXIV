using System;
using System.Numerics;
using System.Threading.Tasks;
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
        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        urlInput = plugin.Config.LastStreamUrl;
        registryUrlInput = plugin.Config.RegistryUrl;
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawStreamSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawCurrentLocationSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawRegistrySection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawSavedHousesSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawPublishedHousesSection();
        DrawStatusFooter();
    }

    private void DrawStreamSection()
    {
        ImGui.TextWrapped("Paste an Icecast / Shoutcast / MP3 stream URL.");
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
    }

    private void DrawCurrentLocationSection()
    {
        ImGui.TextUnformatted("Current Location");
        ImGui.Spacing();

        var key = plugin.CurrentPlotKey;
        if (!key.HasValue)
        {
            ImGui.TextDisabled("Not in housing.");
            return;
        }

        var name = plugin.HousingDetector.GetDisplayName(key.Value);
        ImGui.TextWrapped(name);
        ImGui.Spacing();

        var noUrl = string.IsNullOrWhiteSpace(urlInput);
        if (noUrl) ImGui.BeginDisabled();
        if (ImGui.Button("Save URL for this house (local)"))
        {
            plugin.SaveCurrentHouse(name, urlInput);
            statusLine = $"Saved locally: {name}";
        }
        ImGui.SameLine();
        var canPublish = plugin.RegistryEnabled && !noUrl;
        if (!plugin.RegistryEnabled) ImGui.BeginDisabled();
        if (ImGui.Button("Publish to registry"))
        {
            var keyCanonical = key.Value.Canonical;
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
        if (!plugin.RegistryEnabled) ImGui.EndDisabled();
        if (noUrl) ImGui.EndDisabled();
    }

    private void DrawRegistrySection()
    {
        ImGui.TextUnformatted("Registry");
        ImGui.Spacing();

        ImGui.TextWrapped("Backend URL (e.g. https://clubffxiv-registry.workers.dev):");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##registryUrl", ref registryUrlInput, 512))
        {
            // edited but not yet applied
        }
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

        string? toDelete = null;
        foreach (var (k, entry) in plugin.Config.SavedHouses)
        {
            ImGui.PushID("saved-" + k);
            ImGui.TextUnformatted(entry.DisplayName);
            ImGui.SameLine();
            RightAlignButtons(out var loadX, out var deleteX);
            ImGui.SetCursorPosX(loadX);
            if (ImGui.SmallButton("Load"))
            {
                urlInput = entry.StreamUrl;
                statusLine = $"Loaded URL from {entry.DisplayName}";
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
            {
                toDelete = k;
            }
            ImGui.TextDisabled("  " + entry.StreamUrl);
            ImGui.PopID();
        }
        if (toDelete != null)
        {
            plugin.DeleteSavedHouse(toDelete);
            statusLine = "Removed saved house.";
        }
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

        string? toUnpublish = null;
        foreach (var (k, entry) in plugin.Config.PublishedHouses)
        {
            ImGui.PushID("pub-" + k);
            ImGui.TextUnformatted(entry.DisplayName);
            ImGui.SameLine();
            RightAlignButtons(out var loadX, out var deleteX, "Load", "Unpublish");
            ImGui.SetCursorPosX(loadX);
            if (ImGui.SmallButton("Load"))
            {
                urlInput = entry.StreamUrl;
                statusLine = $"Loaded URL from {entry.DisplayName}";
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Unpublish"))
            {
                toUnpublish = k;
            }
            ImGui.TextDisabled("  " + entry.StreamUrl);
            ImGui.PopID();
        }
        if (toUnpublish != null)
        {
            var k = toUnpublish;
            statusLine = "Unpublishing...";
            _ = Task.Run(async () =>
            {
                try
                {
                    await plugin.UnpublishHouseAsync(k);
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

    private void DrawStatusFooter()
    {
        if (string.IsNullOrEmpty(statusLine)) return;
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(statusLine);
    }

    private static void RightAlignButtons(out float firstX, out float secondX, string first = "Load", string second = "Delete")
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var pad = ImGui.GetStyle().FramePadding.X * 2;
        var firstW = ImGui.CalcTextSize(first).X + pad;
        var secondW = ImGui.CalcTextSize(second).X + pad;
        var startX = ImGui.GetCursorPosX() + avail - firstW - secondW - 6;
        firstX = startX;
        secondX = startX + firstW + 6;
    }
}
