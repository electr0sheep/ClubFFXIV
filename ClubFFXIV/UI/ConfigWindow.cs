using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;

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
        Size = new Vector2(480, 240);
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

        var muteBgm = plugin.Config.MuteGameBgm;
        if (ImGui.Checkbox("Mute FFXIV audio while stream plays", ref muteBgm))
        {
            plugin.Config.MuteGameBgm = muteBgm;
            plugin.Config.Save();
            plugin.ApplyMutePreference();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("Phase 1 mutes the entire FFXIV audio session\n(BGM + SFX + voices). A future version will\nmute only BGM.");
            ImGui.EndTooltip();
        }

        if (!string.IsNullOrEmpty(statusLine))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextWrapped(statusLine);
        }
    }
}
