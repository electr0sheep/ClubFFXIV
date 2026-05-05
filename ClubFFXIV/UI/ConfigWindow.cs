using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string urlInput;
    private string statusLine = "";

    public ConfigWindow(Plugin plugin)
        : base("ClubFFXIV##ClubFFXIVConfig", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(520, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        urlInput = plugin.Config.LastStreamUrl;
    }

    public void Dispose() { }

    public override void Draw()
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
        ImGui.Separator();
        ImGui.Spacing();

        var volume = plugin.Config.Volume;
        if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, "%.2f"))
        {
            plugin.Config.Volume = volume;
            plugin.Config.Save();
            plugin.SetStreamVolume(volume);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Current Location");
        ImGui.Spacing();

        var key = plugin.CurrentPlotKey;
        if (key.HasValue)
        {
            var name = plugin.HousingDetector.GetDisplayName(key.Value);
            ImGui.TextWrapped(name);

            var disabled = string.IsNullOrWhiteSpace(urlInput);
            if (disabled) ImGui.BeginDisabled();
            if (ImGui.Button("Save current URL for this house"))
            {
                plugin.SaveCurrentHouse(name, urlInput);
                statusLine = $"Saved {name} → {urlInput}";
            }
            if (disabled) ImGui.EndDisabled();
        }
        else
        {
            ImGui.TextDisabled("Not in housing.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"Saved Houses ({plugin.Config.SavedHouses.Count})");
        ImGui.Spacing();

        string? toDelete = null;
        foreach (var (k, entry) in plugin.Config.SavedHouses)
        {
            ImGui.PushID(k);

            ImGui.TextUnformatted(entry.DisplayName);
            ImGui.SameLine();

            // Right-align action buttons
            var avail = ImGui.GetContentRegionAvail().X;
            var loadW = ImGui.CalcTextSize("Load").X + ImGui.GetStyle().FramePadding.X * 2 + 4;
            var deleteW = ImGui.CalcTextSize("Delete").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - loadW - deleteW - 6);

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

        if (!string.IsNullOrEmpty(statusLine))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextWrapped(statusLine);
        }
    }
}
