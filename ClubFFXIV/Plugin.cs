using System;
using ClubFFXIV.Audio;
using ClubFFXIV.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ClubFFXIV;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "ClubFFXIV";

    private const string CommandName = "/club";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    public Configuration Config { get; }
    public WindowSystem WindowSystem { get; } = new("ClubFFXIV");

    private readonly ConfigWindow configWindow;
    private readonly StreamPlayer streamPlayer = new();
    private readonly BgmMuter bgmMuter = new();

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);
        streamPlayer.Volume = Config.Volume;

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/club play <url> | /club stop | /club config"
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;

        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();
        streamPlayer.Dispose();
        bgmMuter.Dispose();
    }

    public void PlayStream(string url)
    {
        streamPlayer.Play(url);
        ApplyMutePreference();
    }

    public void StopStream()
    {
        streamPlayer.Stop();
        bgmMuter.Unmute();
    }

    public void SetStreamVolume(float volume) => streamPlayer.Volume = volume;

    public void ApplyMutePreference()
    {
        if (Config.MuteGameBgm && streamPlayer.IsPlaying)
            bgmMuter.Mute();
        else
            bgmMuter.Unmute();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Length == 0)
        {
            OpenConfig();
            return;
        }

        var spaceIdx = trimmed.IndexOf(' ');
        var sub = (spaceIdx < 0 ? trimmed : trimmed[..spaceIdx]).ToLowerInvariant();
        var rest = spaceIdx < 0 ? "" : trimmed[(spaceIdx + 1)..].Trim();

        switch (sub)
        {
            case "play":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    ChatGui.Print("[ClubFFXIV] Usage: /club play <stream-url>");
                    return;
                }
                try
                {
                    Config.LastStreamUrl = rest;
                    Config.Save();
                    PlayStream(rest);
                    ChatGui.Print($"[ClubFFXIV] Playing {rest}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to start stream");
                    ChatGui.PrintError($"[ClubFFXIV] {ex.Message}");
                }
                break;

            case "stop":
                StopStream();
                ChatGui.Print("[ClubFFXIV] Stopped");
                break;

            case "config":
                OpenConfig();
                break;

            default:
                ChatGui.Print("[ClubFFXIV] Usage: /club play <url> | /club stop | /club config");
                break;
        }
    }

    private void DrawUI() => WindowSystem.Draw();

    private void OpenConfig() => configWindow.Toggle();
}
