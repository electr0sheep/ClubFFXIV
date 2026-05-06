using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// First-run wizard offering to whitelist common stream-source domains so
/// the user isn't bombarded with permission prompts for well-known hosts.
/// </summary>
public sealed class SetupWizardWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private static readonly (string Domain, string Description)[] CommonDomains =
    {
        ("twitch.tv",          "Twitch live streams"),
        ("youtube.com",        "YouTube videos & live streams"),
        ("youtu.be",           "YouTube share URLs"),
        ("ice1.somafm.com",    "SomaFM internet radio"),
        ("ice2.somafm.com",    "SomaFM (alternate)"),
        ("ice3.somafm.com",    "SomaFM (alternate)"),
        ("ice4.somafm.com",    "SomaFM (alternate)"),
        ("soundcloud.com",     "SoundCloud tracks & streams"),
        ("api-v2.soundcloud.com", "SoundCloud API streams"),
    };

    private readonly Dictionary<string, bool> selections = new();

    public SetupWizardWindow(Plugin plugin)
        : base("ClubFFXIV — Setup##ClubFFXIVSetup",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.plugin = plugin;
        Size = new Vector2(540, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
        foreach (var (d, _) in CommonDomains) selections[d] = true;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextWrapped(
            "Welcome to ClubFFXIV! For your safety, the plugin asks before playing " +
            "streams from any new domain. You can pre-approve some well-known hosts " +
            "below so you aren't prompted for them later.");
        ImGui.Spacing();
        ImGui.TextDisabled("You can change these any time in /club config → Permissions.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Pre-approve these domains:");
        ImGui.Spacing();

        foreach (var (domain, desc) in CommonDomains)
        {
            var on = selections[domain];
            if (ImGui.Checkbox($"##chk_{domain}", ref on))
                selections[domain] = on;
            ImGui.SameLine();
            ImGui.TextUnformatted(domain);
            ImGui.SameLine();
            ImGui.TextDisabled($"— {desc}");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Done", new Vector2(120, 0)))
        {
            foreach (var (domain, on) in selections)
                if (on) plugin.Config.AllowedDomains.Add(domain);
            plugin.Config.SetupWizardComplete = true;
            plugin.Config.Save();
            IsOpen = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Skip — approve nothing", new Vector2(180, 0)))
        {
            plugin.Config.SetupWizardComplete = true;
            plugin.Config.Save();
            IsOpen = false;
        }
    }
}
