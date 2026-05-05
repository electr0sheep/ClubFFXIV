using System;
using ClubFFXIV.Audio;
using ClubFFXIV.Game;
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
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    public Configuration Config { get; }
    public WindowSystem WindowSystem { get; } = new("ClubFFXIV");
    public HousingDetector HousingDetector { get; } = new();
    public PlotKey? CurrentPlotKey { get; private set; }

    private readonly ConfigWindow configWindow;
    private readonly StreamPlayer streamPlayer = new();
    private DateTime lastHousingCheck = DateTime.MinValue;
    private static readonly TimeSpan HousingCheckInterval = TimeSpan.FromMilliseconds(1000);

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
        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;

        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();
        streamPlayer.Dispose();
    }

    public void PlayStream(string url) => streamPlayer.Play(url);

    public void StopStream() => streamPlayer.Stop();

    public void SetStreamVolume(float volume) => streamPlayer.Volume = volume;

    public bool IsStreamPlaying => streamPlayer.IsPlaying;

    /// <summary>
    /// Save the given URL as the auto-play stream for the player's current house.
    /// No-op if not currently in a house.
    /// </summary>
    public void SaveCurrentHouse(string displayName, string url)
    {
        if (!CurrentPlotKey.HasValue) return;
        Config.SavedHouses[CurrentPlotKey.Value.Canonical] = new ClubEntry
        {
            DisplayName = displayName,
            StreamUrl = url
        };
        Config.Save();
    }

    public void DeleteSavedHouse(string canonicalKey)
    {
        if (Config.SavedHouses.Remove(canonicalKey))
            Config.Save();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Polling instead of TerritoryChanged because HousingManager isn't fully populated
        // by the time TerritoryChanged fires — by polling we naturally pick up the state
        // a frame or two later without scheduling tick callbacks.
        if (DateTime.UtcNow - lastHousingCheck < HousingCheckInterval) return;
        lastHousingCheck = DateTime.UtcNow;

        PlotKey? newKey;
        try
        {
            newKey = HousingDetector.ResolveCurrent();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HousingDetector.ResolveCurrent threw");
            return;
        }

        if (Nullable.Equals(newKey, CurrentPlotKey)) return;

        var previous = CurrentPlotKey;
        CurrentPlotKey = newKey;

        if (previous.HasValue)
        {
            // Left a known house — stop only if we were the ones who started playback.
            if (streamPlayer.IsPlaying)
            {
                StopStream();
                Log.Info($"[ClubFFXIV] Left {previous.Value.Canonical}, stopped stream");
            }
        }

        if (newKey.HasValue && Config.SavedHouses.TryGetValue(newKey.Value.Canonical, out var entry))
        {
            if (string.IsNullOrWhiteSpace(entry.StreamUrl)) return;
            try
            {
                PlayStream(entry.StreamUrl);
                ChatGui.Print($"[ClubFFXIV] Auto-playing: {entry.DisplayName}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Auto-play on house entry failed");
                ChatGui.PrintError($"[ClubFFXIV] Auto-play failed: {ex.Message}");
            }
        }
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
