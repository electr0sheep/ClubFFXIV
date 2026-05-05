using System;
using System.Collections.Generic;
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
    public WardLocation? CurrentWard { get; private set; }
    public PlaybackMode CurrentMode { get; private set; } = PlaybackMode.Off;
    public WardProximity.Result? CurrentProximity { get; private set; }

    private readonly ConfigWindow configWindow;
    private readonly StreamPlayer streamPlayer = new();
    private ClubRegistryClient? registryClient;
    private DjIdentity? djIdentity;
    private DateTime lastHousingCheck = DateTime.MinValue;
    private static readonly TimeSpan HousingCheckInterval = TimeSpan.FromMilliseconds(500);

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);
        streamPlayer.MasterVolume = Config.Volume;

        RebuildRegistryClient();
        TryLoadDjIdentity();

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/club play <url> | /club stop | /club calibrate | /club config",
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

    public void PlayStream(string url)
    {
        streamPlayer.BypassSpatial();
        streamPlayer.Play(url);
        CurrentMode = PlaybackMode.Manual;
    }

    public void StopStream()
    {
        streamPlayer.Stop();
        CurrentMode = PlaybackMode.Off;
        CurrentProximity = null;
    }

    public void SetStreamVolume(float volume) => streamPlayer.MasterVolume = volume;
    public bool IsStreamPlaying => streamPlayer.IsPlaying;
    public string? CurrentStreamUrl => streamPlayer.CurrentUrl;

    public string? DjId => djIdentity?.DjId;
    public bool RegistryEnabled => registryClient != null;

    public void SaveCurrentHouse(string displayName, string url)
    {
        if (!CurrentPlotKey.HasValue) return;
        var key = CurrentPlotKey.Value.Canonical;
        if (!Config.SavedHouses.TryGetValue(key, out var existing))
            existing = new ClubEntry();
        existing.DisplayName = displayName;
        existing.StreamUrl = url;
        Config.SavedHouses[key] = existing;
        Config.Save();
    }

    public void DeleteSavedHouse(string canonicalKey)
    {
        if (Config.SavedHouses.Remove(canonicalKey))
            Config.Save();
    }

    public void RebuildRegistryClient()
    {
        registryClient?.Dispose();
        registryClient = string.IsNullOrWhiteSpace(Config.RegistryUrl)
            ? null
            : new ClubRegistryClient(Config.RegistryUrl);
    }

    public DjIdentity EnsureDjIdentity()
    {
        if (djIdentity != null) return djIdentity;
        djIdentity = DjIdentity.Generate();
        Config.DjPrivateKeyBase64 = djIdentity.ExportPrivateKeyBase64();
        Config.Save();
        return djIdentity;
    }

    public async Task PublishCurrentHouseAsync(string displayName, string streamUrl)
    {
        if (registryClient == null)
            throw new InvalidOperationException("Registry URL not set");
        if (!CurrentPlotKey.HasValue)
            throw new InvalidOperationException("Not currently in a house");

        var dj = EnsureDjIdentity();
        var key = CurrentPlotKey.Value.Canonical;
        await registryClient.PublishAsync(key, streamUrl, displayName, dj);

        if (!Config.PublishedHouses.TryGetValue(key, out var entry))
            entry = new ClubEntry();
        entry.DisplayName = displayName;
        entry.StreamUrl = streamUrl;
        Config.PublishedHouses[key] = entry;
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

    /// <summary>
    /// Records the player's current world position as the door coordinates for the
    /// given canonical key (must already exist in SavedHouses or PublishedHouses).
    /// Player must be standing in an outdoor ward.
    /// </summary>
    public bool CalibrateDoor(string canonicalKey)
    {
        var ward = HousingDetector.ResolveOutdoor();
        var pos = HousingDetector.PlayerPosition();
        if (ward == null || pos == null)
        {
            ChatGui.PrintError("[ClubFFXIV] Calibrate: must be standing in an outdoor ward.");
            return false;
        }

        bool wrote = false;
        if (Config.SavedHouses.TryGetValue(canonicalKey, out var saved))
        {
            saved.DoorPosition = new Position3(pos.Value);
            saved.DoorTerritoryType = ward.Value.TerritoryType;
            saved.DoorWard = ward.Value.Ward;
            wrote = true;
        }
        if (Config.PublishedHouses.TryGetValue(canonicalKey, out var pub))
        {
            pub.DoorPosition = new Position3(pos.Value);
            pub.DoorTerritoryType = ward.Value.TerritoryType;
            pub.DoorWard = ward.Value.Ward;
            wrote = true;
        }

        if (!wrote)
        {
            ChatGui.PrintError($"[ClubFFXIV] Calibrate: no house with key {canonicalKey}");
            return false;
        }

        Config.Save();
        ChatGui.Print($"[ClubFFXIV] Door calibrated at ({pos.Value.X:F1}, {pos.Value.Y:F1}, {pos.Value.Z:F1})");
        return true;
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

        try
        {
            UpdateLocationState();
            DriveAudio();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OnFrameworkUpdate failed");
        }
    }

    private void UpdateLocationState()
    {
        var newPlot = HousingDetector.ResolveCurrent();
        var newWard = HousingDetector.ResolveOutdoor();

        if (!Nullable.Equals(newPlot, CurrentPlotKey))
        {
            CurrentPlotKey = newPlot;
        }

        if (!Nullable.Equals(newWard, CurrentWard))
        {
            CurrentWard = newWard;
        }
    }

    private void DriveAudio()
    {
        // Indoor takes priority — full quality auto-play if there's a saved/registry stream.
        if (CurrentPlotKey.HasValue)
        {
            HandleIndoorMode(CurrentPlotKey.Value);
            return;
        }

        // Outdoor ward — spatial proximity scan.
        if (CurrentWard.HasValue)
        {
            HandleOutdoorMode(CurrentWard.Value);
            return;
        }

        // Neither indoor nor outdoor housing — stop any auto-play.
        if (CurrentMode is PlaybackMode.Indoor or PlaybackMode.Outdoor)
        {
            streamPlayer.Stop();
            CurrentMode = PlaybackMode.Off;
            CurrentProximity = null;
        }
    }

    private void HandleIndoorMode(PlotKey key)
    {
        // If we're already in indoor mode for this house, nothing to do.
        if (CurrentMode == PlaybackMode.Indoor && streamPlayer.IsPlaying)
        {
            CurrentProximity = null;
            return;
        }

        // Stop any previous (outdoor) playback before switching modes.
        if (streamPlayer.IsPlaying) streamPlayer.Stop();

        // Local saved entry wins.
        if (Config.SavedHouses.TryGetValue(key.Canonical, out var saved)
            && !string.IsNullOrWhiteSpace(saved.StreamUrl))
        {
            StartIndoor(saved.StreamUrl, saved.DisplayName);
            return;
        }

        // Otherwise registry, fire-and-forget.
        if (Config.AutoQueryRegistry && registryClient != null)
        {
            _ = QueryRegistryAndStartIndoor(key);
        }
    }

    private async Task QueryRegistryAndStartIndoor(PlotKey key)
    {
        try
        {
            var record = await registryClient!.GetAsync(key.Canonical);
            if (record == null) return;
            if (!Nullable.Equals(CurrentPlotKey, key)) return; // moved
            StartIndoor(record.StreamUrl, record.DisplayName);
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClubFFXIV] Registry lookup failed: {ex.Message}");
        }
    }

    private void StartIndoor(string url, string displayName)
    {
        try
        {
            streamPlayer.BypassSpatial();
            streamPlayer.Play(url);
            CurrentMode = PlaybackMode.Indoor;
            ChatGui.Print($"[ClubFFXIV] Auto-playing: {displayName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Indoor auto-play failed");
            ChatGui.PrintError($"[ClubFFXIV] Auto-play failed: {ex.Message}");
        }
    }

    private void HandleOutdoorMode(WardLocation ward)
    {
        var pos = HousingDetector.PlayerPosition();
        if (pos == null) return;

        var candidates = EnumerateLocalCandidates(ward);
        var result = WardProximity.FindClosest(
            pos.Value,
            candidates,
            Config.SpatialFalloffDistance,
            Config.SpatialFullVolumeDistance);

        if (result == null)
        {
            // Out of range of any calibrated club — stop spatial playback if running.
            if (CurrentMode == PlaybackMode.Outdoor)
            {
                streamPlayer.Stop();
                CurrentMode = PlaybackMode.Off;
                CurrentProximity = null;
            }
            return;
        }

        var r = result.Value;
        var cutoff = WardProximity.NearnessToCutoff(
            r.NormalizedNearness,
            Config.SpatialMinCutoffHz,
            Config.SpatialMaxCutoffHz);

        // Different club is now closest — switch streams.
        if (CurrentMode != PlaybackMode.Outdoor
            || streamPlayer.CurrentUrl != r.Candidate.StreamUrl)
        {
            try
            {
                streamPlayer.Stop();
                streamPlayer.SetSpatial(r.NormalizedNearness, cutoff);
                streamPlayer.Play(r.Candidate.StreamUrl);
                CurrentMode = PlaybackMode.Outdoor;
                ChatGui.Print($"[ClubFFXIV] Approaching: {r.Candidate.DisplayName}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Spatial stream start failed");
                CurrentMode = PlaybackMode.Off;
                CurrentProximity = null;
                return;
            }
        }
        else
        {
            streamPlayer.SetSpatial(r.NormalizedNearness, cutoff);
        }

        CurrentProximity = r;
    }

    private IEnumerable<WardProximity.Candidate> EnumerateLocalCandidates(WardLocation ward)
    {
        foreach (var (key, entry) in Config.SavedHouses)
            if (TryToCandidate(key, entry, ward, out var c)) yield return c;
        foreach (var (key, entry) in Config.PublishedHouses)
            if (TryToCandidate(key, entry, ward, out var c)) yield return c;
    }

    private static bool TryToCandidate(
        string key, ClubEntry entry, WardLocation ward, out WardProximity.Candidate candidate)
    {
        candidate = default;
        if (entry.DoorPosition == null) return false;
        if (entry.DoorTerritoryType != ward.TerritoryType) return false;
        if (entry.DoorWard != ward.Ward) return false;
        if (string.IsNullOrWhiteSpace(entry.StreamUrl)) return false;
        candidate = new WardProximity.Candidate(
            key, entry.DisplayName, entry.StreamUrl, entry.DoorPosition.ToVec());
        return true;
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

            case "calibrate":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    ChatGui.Print("[ClubFFXIV] Usage: /club calibrate <plotKey>");
                    return;
                }
                CalibrateDoor(rest);
                break;

            case "config":
                OpenConfig();
                break;

            default:
                ChatGui.Print("[ClubFFXIV] Usage: /club play <url> | /club stop | /club calibrate | /club config");
                break;
        }
    }

    private void DrawUI() => WindowSystem.Draw();

    private void OpenConfig() => configWindow.Toggle();
}

public enum PlaybackMode
{
    Off,
    Manual,
    Indoor,
    Outdoor,
}
