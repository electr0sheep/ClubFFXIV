using System;
using System.Threading;
using System.Threading.Tasks;
using ClubFFXIV.Audio;
using ClubFFXIV.Game;
using ClubFFXIV.Network;
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
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    public Configuration Config { get; }
    public WindowSystem WindowSystem { get; } = new("ClubFFXIV");
    public HousingDetector HousingDetector { get; } = new();
    public PlotKey? CurrentPlotKey { get; private set; }

    private readonly ConfigWindow configWindow;
    private readonly StreamPlayer streamPlayer = new();
    private ClubRegistryClient? registryClient;
    private DjIdentity? djIdentity;
    private DateTime lastHousingCheck = DateTime.MinValue;
    private static readonly TimeSpan HousingCheckInterval = TimeSpan.FromMilliseconds(1000);

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);
        streamPlayer.Volume = Config.Volume;

        RebuildRegistryClient();
        TryLoadDjIdentity();

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/club play <url> | /club stop | /club config",
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
        registryClient?.Dispose();
        djIdentity?.Dispose();
    }

    public void PlayStream(string url) => streamPlayer.Play(url);
    public void StopStream() => streamPlayer.Stop();
    public void SetStreamVolume(float volume) => streamPlayer.Volume = volume;
    public bool IsStreamPlaying => streamPlayer.IsPlaying;

    public string? DjId => djIdentity?.DjId;
    public bool RegistryEnabled => registryClient != null;

    public void SaveCurrentHouse(string displayName, string url)
    {
        if (!CurrentPlotKey.HasValue) return;
        Config.SavedHouses[CurrentPlotKey.Value.Canonical] = new ClubEntry
        {
            DisplayName = displayName,
            StreamUrl = url,
        };
        Config.Save();
    }

    public void DeleteSavedHouse(string canonicalKey)
    {
        if (Config.SavedHouses.Remove(canonicalKey))
            Config.Save();
    }

    /// <summary>
    /// Called by the UI when the registry URL changes. Recreates the HTTP client
    /// against the new base URL (or disables it if blank).
    /// </summary>
    public void RebuildRegistryClient()
    {
        registryClient?.Dispose();
        registryClient = string.IsNullOrWhiteSpace(Config.RegistryUrl)
            ? null
            : new ClubRegistryClient(Config.RegistryUrl);
    }

    /// <summary>
    /// Generates the DJ keypair on first use. Subsequent calls are no-ops.
    /// </summary>
    public DjIdentity EnsureDjIdentity()
    {
        if (djIdentity != null) return djIdentity;
        djIdentity = DjIdentity.Generate();
        Config.DjPrivateKeyBase64 = djIdentity.ExportPrivateKeyBase64();
        Config.Save();
        return djIdentity;
    }

    /// <summary>
    /// Publish (or update) the current house's stream URL to the registry.
    /// Creates a DJ keypair on demand if one doesn't exist.
    /// </summary>
    public async Task PublishCurrentHouseAsync(string displayName, string streamUrl)
    {
        if (registryClient == null)
            throw new InvalidOperationException("Registry URL not set");
        if (!CurrentPlotKey.HasValue)
            throw new InvalidOperationException("Not currently in a house");

        var dj = EnsureDjIdentity();
        var key = CurrentPlotKey.Value.Canonical;
        await registryClient.PublishAsync(key, streamUrl, displayName, dj);

        Config.PublishedHouses[key] = new ClubEntry { DisplayName = displayName, StreamUrl = streamUrl };
        Config.Save();
    }

    public async Task UnpublishHouseAsync(string canonicalKey)
    {
        if (registryClient == null)
            throw new InvalidOperationException("Registry URL not set");
        if (djIdentity == null)
            throw new InvalidOperationException("No DJ identity — nothing to unpublish");

        await registryClient.DeleteAsync(canonicalKey, djIdentity);
        if (Config.PublishedHouses.Remove(canonicalKey))
            Config.Save();
    }

    private void TryLoadDjIdentity()
    {
        if (string.IsNullOrWhiteSpace(Config.DjPrivateKeyBase64)) return;
        try
        {
            djIdentity = DjIdentity.FromPrivateKeyBase64(Config.DjPrivateKeyBase64);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load DJ identity from config — clearing");
            Config.DjPrivateKeyBase64 = "";
            Config.Save();
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
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

        if (previous.HasValue && streamPlayer.IsPlaying)
        {
            StopStream();
            Log.Info($"[ClubFFXIV] Left {previous.Value.Canonical}, stopped stream");
        }

        if (!newKey.HasValue) return;

        // Local saved entry always wins — it's the user's explicit override.
        if (Config.SavedHouses.TryGetValue(newKey.Value.Canonical, out var saved)
            && !string.IsNullOrWhiteSpace(saved.StreamUrl))
        {
            TryAutoPlay(saved.StreamUrl, saved.DisplayName);
            return;
        }

        // Otherwise fall back to the registry, fire-and-forget.
        if (Config.AutoQueryRegistry && registryClient != null)
        {
            _ = QueryRegistryAndAutoPlay(newKey.Value);
        }
    }

    private async Task QueryRegistryAndAutoPlay(PlotKey key)
    {
        try
        {
            var record = await registryClient!.GetAsync(key.Canonical);
            if (record == null) return;
            // The player may have left the house in the time the request took.
            if (!Nullable.Equals(CurrentPlotKey, key)) return;
            TryAutoPlay(record.StreamUrl, record.DisplayName);
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClubFFXIV] Registry lookup failed: {ex.Message}");
        }
    }

    private void TryAutoPlay(string url, string displayName)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            PlayStream(url);
            ChatGui.Print($"[ClubFFXIV] Auto-playing: {displayName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Auto-play failed");
            ChatGui.PrintError($"[ClubFFXIV] Auto-play failed: {ex.Message}");
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
