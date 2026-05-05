using System;
using System.Collections.Generic;
using System.Numerics;
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
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    public Configuration Config { get; }
    public WindowSystem WindowSystem { get; } = new("ClubFFXIV");
    public HousingDetector HousingDetector { get; } = new();

    public PlotKey? CurrentPlotKey { get; private set; }
    public WardLocation? CurrentWard { get; private set; }
    public PlaybackMode CurrentMode { get; private set; } = PlaybackMode.Off;
    public WardProximity.Result? CurrentProximity { get; private set; }
    /// <summary>
    /// House ownership status for the player's current location, refreshed each
    /// framework tick. Cached so off-thread callers (publish flow, etc.) can
    /// safely read it — calling HousingDetector.CheckOwnership directly from
    /// a Task.Run continuation would access ObjectTable.LocalPlayer off the
    /// framework thread and crash.
    /// </summary>
    public HouseOwnership CurrentOwnership { get; private set; } = HouseOwnership.Unknown;

    private readonly ConfigWindow configWindow;
    private readonly HelpWindow helpWindow = new();
    public BinaryManager Binaries { get; }
    private readonly StreamPlayer streamPlayer;
    private readonly GameBgmMuter bgmMuter = new();
    private ClubRegistryClient? registryClient;
    private DateTime lastBinaryUpdateCheck = DateTime.MinValue;
    private static readonly TimeSpan BinaryUpdateInterval = TimeSpan.FromDays(2);
    private DjIdentity? djIdentity;
    private DateTime lastHousingCheck = DateTime.MinValue;
    private static readonly TimeSpan HousingCheckInterval = TimeSpan.FromMilliseconds(500);

    // Cancels any in-flight stream construction when a newer one starts.
    private System.Threading.CancellationTokenSource? streamStartCts;
    // URL that's currently being started (chain construction in flight).
    // Prevents the next framework tick from spawning a duplicate start.
    private string? pendingStartUrl;

    // Ward listing cache: keyed by (worldId, territoryType, ward), TTL 60s.
    private readonly Dictionary<string, CachedWardListing> wardCache = new();
    private static readonly TimeSpan WardCacheTtl = TimeSpan.FromSeconds(60);
    private string? wardFetchInFlight;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);

        Binaries = new BinaryManager(PluginInterface.GetPluginConfigDirectory());
        streamPlayer = new StreamPlayer(Binaries);
        streamPlayer.MasterVolume = Config.Volume;
        lastBinaryUpdateCheck = Config.BinariesLastChecked;

        RebuildRegistryClient();
        TryLoadDjIdentity();

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(helpWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/club play <url> | /club stop | /club calibrate <key> | /club config",
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
        helpWindow.Dispose();
        streamPlayer.Dispose();
        bgmMuter.Dispose();
        registryClient?.Dispose();
        djIdentity?.Dispose();
    }

    public void ToggleHelp() => helpWindow.Toggle();

    public void PlayStream(string url)
    {
        streamPlayer.BypassSpatial();
        // Optimistic: set mode now so UI reflects intent. The async chain build
        // happens in the background — if it fails we revert.
        CurrentMode = PlaybackMode.Manual;
        _ = StartStreamAsync(url, PlaybackMode.Manual, $"Stream: {url}");
    }

    public void StopStream()
    {
        streamStartCts?.Cancel();
        streamPlayer.Stop();
        CurrentMode = PlaybackMode.Off;
        CurrentProximity = null;
    }

    /// <summary>
    /// Starts a stream off the framework thread. Cancels any prior in-flight start.
    /// On completion, sets CurrentMode and prints a chat notification.
    /// </summary>
    private async Task StartStreamAsync(string url, PlaybackMode targetMode, string displayName)
    {
        streamStartCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        streamStartCts = cts;
        pendingStartUrl = url;

        Log.Info($"[ClubFFXIV] Starting stream ({targetMode}): {url}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await streamPlayer.PlayAsync(url, cts.Token);
            if (cts.IsCancellationRequested) return;
            CurrentMode = targetMode;
            Log.Info($"[ClubFFXIV] Stream ready in {sw.ElapsedMilliseconds}ms ({targetMode})");
            ChatGui.Print($"[ClubFFXIV] {(targetMode == PlaybackMode.Outdoor ? "Approaching" : "Playing")}: {displayName}");
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[ClubFFXIV] Stream start cancelled after {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[ClubFFXIV] Stream start failed after {sw.ElapsedMilliseconds}ms");
            ChatGui.PrintError($"[ClubFFXIV] {ex.Message}");
            if (CurrentMode == targetMode) CurrentMode = PlaybackMode.Off;
        }
        finally
        {
            if (pendingStartUrl == url) pendingStartUrl = null;
        }
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
        wardCache.Clear();
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

        // Local ownership gate — read from the framework-tick-cached value since
        // we're running off the framework thread (Task.Run from the UI button).
        // Unknown is allowed (don't lock out users on API mismatch); only confirmed
        // NotOwner is blocked, and even that can be overridden via config.
        if (CurrentOwnership == HouseOwnership.NotOwner && !Config.AllowPublishWithoutOwnership)
        {
            throw new InvalidOperationException(
                "You don't appear to own this house. " +
                "Enable \"Allow publish without ownership check\" in /club config to override.");
        }

        var dj = EnsureDjIdentity();
        var key = CurrentPlotKey.Value.Canonical;

        // Pull door coords from local entry if calibrated.
        DoorPayload? door = null;
        if (Config.PublishedHouses.TryGetValue(key, out var existing) && HasDoor(existing))
            door = ToDoorPayload(existing);
        else if (Config.SavedHouses.TryGetValue(key, out var saved) && HasDoor(saved))
            door = ToDoorPayload(saved);

        await registryClient.PublishAsync(key, streamUrl, displayName, dj, door);

        if (existing == null)
        {
            existing = new ClubEntry();
            Config.PublishedHouses[key] = existing;
        }
        existing.DisplayName = displayName;
        existing.StreamUrl = streamUrl;
        Config.Save();

        InvalidateWardCacheForDoor(door);
    }

    public async Task UnpublishHouseAsync(string canonicalKey)
    {
        if (registryClient == null)
            throw new InvalidOperationException("Registry URL not set");
        if (djIdentity == null)
            throw new InvalidOperationException("No DJ identity — nothing to unpublish");

        await registryClient.DeleteAsync(canonicalKey, djIdentity);

        if (Config.PublishedHouses.TryGetValue(canonicalKey, out var entry))
        {
            InvalidateWardCacheForDoor(ToDoorPayload(entry));
            Config.PublishedHouses.Remove(canonicalKey);
            Config.Save();
        }
    }

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

            // Re-publish with new door coords so other listeners auto-discover.
            if (registryClient != null && djIdentity != null)
            {
                var doorPayload = ToDoorPayload(pub);
                var dn = pub.DisplayName;
                var url = pub.StreamUrl;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await registryClient.PublishAsync(canonicalKey, url, dn, djIdentity, doorPayload);
                        InvalidateWardCacheForDoor(doorPayload);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[ClubFFXIV] Auto-republish after calibrate failed: {ex.Message}");
                    }
                });
            }
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
            ApplyAudioPolicy();
            MaybeCheckBinaryUpdates();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OnFrameworkUpdate failed");
        }
    }

    /// <summary>
    /// Periodic background check for yt-dlp updates. yt-dlp self-updates via -U;
    /// ffmpeg is checked separately and rarely needs updates. Runs at most once
    /// per BinaryUpdateInterval and only if AutoUpdateBinaries is on.
    /// </summary>
    private void MaybeCheckBinaryUpdates()
    {
        if (!Config.AutoUpdateBinaries) return;
        if (!Binaries.Ready) return; // initial install handled lazily on first Twitch URL
        if (DateTime.UtcNow - lastBinaryUpdateCheck < BinaryUpdateInterval) return;

        lastBinaryUpdateCheck = DateTime.UtcNow;
        Config.BinariesLastChecked = DateTime.UtcNow;
        Config.Save();

        _ = Task.Run(async () =>
        {
            try
            {
                var newVersion = await Binaries.UpdateYtDlpAsync();
                Log.Info($"[ClubFFXIV] yt-dlp now at: {newVersion}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClubFFXIV] Binary update check failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// After audio state is settled for the tick, apply cross-cutting policies:
    /// game BGM muting (when our stream plays) and focus muting (when game unfocused).
    /// </summary>
    private void ApplyAudioPolicy()
    {
        // Stream output mute when game is unfocused.
        streamPlayer.Muted = Config.MuteStreamWhenUnfocused && !WindowFocus.IsGameFocused();

        // Game BGM mute only when stream is the primary audio source (indoor / manual).
        // Outdoor proximity is meant to layer *over* the world's own BGM, not replace it.
        var streamIsPrimary = streamPlayer.IsPlaying
            && CurrentMode is PlaybackMode.Indoor or PlaybackMode.Manual;
        if (Config.MuteGameBgmWhilePlaying && streamIsPrimary)
            bgmMuter.Mute();
        else
            bgmMuter.Unmute();
    }

    private void UpdateLocationState()
    {
        var newPlot = HousingDetector.ResolveCurrent();
        var newWard = HousingDetector.ResolveOutdoor();

        if (!Nullable.Equals(newPlot, CurrentPlotKey)) CurrentPlotKey = newPlot;

        if (!Nullable.Equals(newWard, CurrentWard))
        {
            CurrentWard = newWard;
        }

        // Cache ownership while we're on the framework thread — publish flow
        // runs in a Task.Run continuation and can't safely call CheckOwnership.
        CurrentOwnership = newPlot.HasValue
            ? HousingDetector.CheckOwnership()
            : HouseOwnership.Unknown;

        // Always make sure we have a fresh listing for the current ward.
        // EnsureWardListingAsync is a no-op when cache is fresh or a fetch is in flight,
        // so calling it every tick is free.
        if (CurrentWard.HasValue && registryClient != null && Config.AutoQueryRegistry)
            _ = EnsureWardListingAsync(CurrentWard.Value);
    }

    private void DriveAudio()
    {
        if (CurrentPlotKey.HasValue)
        {
            HandleIndoorMode(CurrentPlotKey.Value);
            return;
        }

        if (CurrentWard.HasValue)
        {
            HandleOutdoorMode(CurrentWard.Value);
            return;
        }

        if (CurrentMode is PlaybackMode.Indoor or PlaybackMode.Outdoor)
        {
            streamPlayer.Stop();
            CurrentMode = PlaybackMode.Off;
            CurrentProximity = null;
        }
    }

    private void HandleIndoorMode(PlotKey key)
    {
        if (CurrentMode == PlaybackMode.Indoor && streamPlayer.IsPlaying)
        {
            CurrentProximity = null;
            return;
        }

        if (Config.SavedHouses.TryGetValue(key.Canonical, out var saved)
            && !string.IsNullOrWhiteSpace(saved.StreamUrl))
        {
            EnterIndoor(saved.StreamUrl, saved.DisplayName);
            return;
        }

        if (Config.AutoQueryRegistry && registryClient != null)
        {
            _ = QueryRegistryAndEnterIndoor(key);
        }
    }

    private async Task QueryRegistryAndEnterIndoor(PlotKey key)
    {
        try
        {
            var record = await registryClient!.GetAsync(key.Canonical);
            if (record == null) return;
            if (!Nullable.Equals(CurrentPlotKey, key)) return;
            EnterIndoor(record.StreamUrl, record.DisplayName);
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClubFFXIV] Registry lookup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Enter indoor playback for the given URL. If we were already playing this exact
    /// URL outdoors (player walked from the door into the house), drop the spatial
    /// chain in place — no restart, no rebuffering.
    /// </summary>
    private void EnterIndoor(string url, string displayName)
    {
        // Seamless: same URL was already streaming outdoors.
        if (streamPlayer.IsPlaying && streamPlayer.CurrentUrl == url)
        {
            streamPlayer.BypassSpatial();
            CurrentMode = PlaybackMode.Indoor;
            CurrentProximity = null;
            return;
        }

        // Already trying to start this exact URL — let it finish.
        if (pendingStartUrl == url) return;

        streamPlayer.BypassSpatial();
        _ = StartStreamAsync(url, PlaybackMode.Indoor, displayName);
    }

    private void HandleOutdoorMode(WardLocation ward)
    {
        var pos = HousingDetector.PlayerPosition();
        if (pos == null) return;

        var candidates = EnumerateCandidates(ward);
        var result = WardProximity.FindClosest(
            pos.Value,
            candidates,
            Config.SpatialStreamDistance,
            Config.SpatialFalloffDistance,
            Config.SpatialFullVolumeDistance);

        // Always store the proximity result (even if out of range) so the UI can
        // show how far the closest club is — useful for calibration & debugging.
        CurrentProximity = result;

        // Streaming is the broader range — keep the stream alive even outside
        // the audible band so the buffer is primed when the player crosses it.
        if (result == null || !result.Value.Streaming)
        {
            if (CurrentMode == PlaybackMode.Outdoor)
            {
                streamPlayer.Stop();
                CurrentMode = PlaybackMode.Off;
            }
            return;
        }

        var r = result.Value;
        var cutoff = WardProximity.NearnessToCutoff(
            r.NormalizedNearness,
            Config.SpatialMinCutoffHz,
            Config.SpatialMaxCutoffHz);

        streamPlayer.SetSpatial(r.NormalizedNearness, cutoff);

        var needNewStream = streamPlayer.CurrentUrl != r.Candidate.StreamUrl
            || (CurrentMode != PlaybackMode.Outdoor && !streamPlayer.IsPlaying);

        if (needNewStream && pendingStartUrl != r.Candidate.StreamUrl)
        {
            _ = StartStreamAsync(r.Candidate.StreamUrl, PlaybackMode.Outdoor, r.Candidate.DisplayName);
        }
        else if (CurrentMode != PlaybackMode.Outdoor && streamPlayer.IsPlaying)
        {
            CurrentMode = PlaybackMode.Outdoor;
        }
    }

    private IEnumerable<WardProximity.Candidate> EnumerateCandidates(WardLocation ward)
    {
        // Local entries first — DJ's own calibrations win over registry.
        foreach (var (key, entry) in Config.SavedHouses)
            if (TryLocalToCandidate(key, entry, ward, out var c)) yield return c;
        foreach (var (key, entry) in Config.PublishedHouses)
            if (TryLocalToCandidate(key, entry, ward, out var c)) yield return c;

        // Registry-discovered, deduplicated against local keys.
        var seen = new HashSet<string>();
        foreach (var (key, _) in Config.SavedHouses) seen.Add(key);
        foreach (var (key, _) in Config.PublishedHouses) seen.Add(key);

        if (TryGetCachedWard(ward, out var cached))
        {
            foreach (var entry in cached.Clubs)
            {
                if (seen.Contains(entry.PlotKey)) continue;
                yield return new WardProximity.Candidate(
                    entry.PlotKey,
                    entry.DisplayName,
                    entry.StreamUrl,
                    new Vector3(entry.Door.X, entry.Door.Y, entry.Door.Z));
            }
        }
    }

    private static bool TryLocalToCandidate(
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

    // ---- Ward listing cache ----

    private bool TryGetCachedWard(WardLocation ward, out WardListing listing)
    {
        var k = WardCacheKey(ward);
        if (wardCache.TryGetValue(k, out var entry)
            && DateTime.UtcNow - entry.FetchedAt < WardCacheTtl)
        {
            listing = entry.Listing;
            return true;
        }
        listing = new WardListing();
        return false;
    }

    private async Task EnsureWardListingAsync(WardLocation ward)
    {
        if (registryClient == null) return;
        var k = WardCacheKey(ward);
        if (wardFetchInFlight == k) return;
        if (wardCache.TryGetValue(k, out var existing)
            && DateTime.UtcNow - existing.FetchedAt < WardCacheTtl) return;

        wardFetchInFlight = k;
        try
        {
            var worldId = PlayerState.CurrentWorld.RowId;
            Log.Info($"[ClubFFXIV] Fetching ward listing: world={worldId} territory={ward.TerritoryType} ward={ward.Ward}");
            var listing = await registryClient.GetWardAsync(worldId, ward.TerritoryType, ward.Ward);
            wardCache[k] = new CachedWardListing(DateTime.UtcNow, listing);
            Log.Info($"[ClubFFXIV] Ward listing fetched: {listing.Clubs.Count} club(s)");
        }
        catch (Exception ex)
        {
            // Use Log.Error so the inner exception chain is fully serialized.
            Log.Error(ex, "[ClubFFXIV] Ward listing fetch failed");
        }
        finally
        {
            wardFetchInFlight = null;
        }
    }

    private string WardCacheKey(WardLocation ward)
    {
        var worldId = PlayerState.CurrentWorld.RowId;
        return $"{worldId}:{ward.TerritoryType}:{ward.Ward}";
    }

    private void InvalidateWardCacheForDoor(DoorPayload? door)
    {
        if (door == null) return;
        var worldId = PlayerState.CurrentWorld.RowId;
        wardCache.Remove($"{worldId}:{door.TerritoryType}:{door.Ward}");
    }

    private static bool HasDoor(ClubEntry e) =>
        e.DoorPosition != null && e.DoorTerritoryType.HasValue && e.DoorWard.HasValue;

    private static DoorPayload ToDoorPayload(ClubEntry e) => new()
    {
        X = e.DoorPosition!.X,
        Y = e.DoorPosition.Y,
        Z = e.DoorPosition.Z,
        TerritoryType = e.DoorTerritoryType ?? 0,
        Ward = e.DoorWard ?? 0,
    };

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
                ChatGui.Print("[ClubFFXIV] Usage: /club play <url> | /club stop | /club calibrate <key> | /club config");
                break;
        }
    }

    private void DrawUI() => WindowSystem.Draw();

    private void OpenConfig() => configWindow.Toggle();

    private readonly record struct CachedWardListing(DateTime FetchedAt, WardListing Listing);
}

public enum PlaybackMode
{
    Off,
    Manual,
    Indoor,
    Outdoor,
}
