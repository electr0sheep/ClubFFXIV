using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using ClubFFXIV.Audio;
using ClubFFXIV.Game;
using ClubFFXIV.Network;
using ClubFFXIV.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ClubFFXIV;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "ClubFFXIV";

    private const string CommandName = "/pclub";

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
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;

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
    private readonly UrlPermissionWindow permissionWindow;
    private readonly SetupWizardWindow setupWizard;
    private readonly DirectoryWindow directoryWindow;
    /// <summary>
    /// Single shared form window for create / edit on both local overrides
    /// and registry clubs. Public so the My Clubs tab and the My Houses
    /// table can call <see cref="ClubFormWindow.OpenLocalCreate"/> etc.
    /// </summary>
    public ClubFormWindow ClubFormWindow { get; }
    public BinaryManager Binaries { get; }
    public UrlPermissions Permissions { get; }
    private readonly StreamPlayer streamPlayer;
#if DEBUG
    // Lazily created: the WaveOutEvent inside MultiStreamPlayer is non-trivial,
    // so we only build it once the user opts in via the Advanced tab toggle.
    private MultiStreamPlayer? multiStreamPlayer;
#endif
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
    // True when the user explicitly hit Stop — suppresses auto-play (Indoor / Outdoor)
    // until they Play again or change territory. Without this, Stop in a registered
    // house would immediately resume Indoor auto-play on the next tick.
    private bool userInhibitsAutoPlay;
    // Suppresses repeated chat warnings when an auto-play target needs yt-dlp /
    // ffmpeg but the user hasn't installed them. We tell them once per session,
    // then silently no-op subsequent ticks instead of spamming /xllog.
    private bool warnedAboutMissingBinaries;

    // Per-URL failure tracking with backoff. The auto-play loop runs every
    // 500ms, so a persistent failure (404, DNS NXDOMAIN, refused, slow 5xx,
    // yt-dlp/ffmpeg crash, etc.) without backoff would log an Error and
    // attempt a fresh stream start at 2 Hz indefinitely. Backoff schedule
    // is short for the first couple of retries (transient blips recover
    // fast) and stretches to 5 min after we've concluded the URL is broken.
    // Mutated from off-thread Task continuations + read from the framework
    // thread, so all access is gated by failureLock.
    private sealed class StreamFailureState
    {
        public int ConsecutiveFailures;
        public DateTime LastAttemptUtc;
        public DateTime NextAttemptUtc;
        public string LastErrorMessage = "";
    }
    private readonly Dictionary<string, StreamFailureState> streamFailures = new();
    private readonly object failureLock = new();
    private const int NotifyAfterFailures = 3;

    // Ward listing cache: keyed by (worldId, territoryType, ward), TTL 60s.
    private readonly Dictionary<string, CachedWardListing> wardCache = new();
    private static readonly TimeSpan WardCacheTtl = TimeSpan.FromSeconds(60);
    private string? wardFetchInFlight;

    // Public directory cache: a single global blob, fetched lazily when the
    // user opens the Public Directory panel. TTL 60s; the UI also exposes a
    // manual Refresh button.
    private DirectoryListing? directoryCache;
    private DateTime directoryCacheFetchedAt = DateTime.MinValue;
    private static readonly TimeSpan DirectoryCacheTtl = TimeSpan.FromSeconds(60);
    private bool directoryFetchInFlight;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);

        Binaries = new BinaryManager(PluginInterface.GetPluginConfigDirectory());
        Permissions = new UrlPermissions(Config);
        streamPlayer = new StreamPlayer(Binaries);
        streamPlayer.MasterVolume = Config.Volume;
        streamPlayer.StreamNaturallyEnded += OnStreamNaturallyEnded;
        lastBinaryUpdateCheck = Config.BinariesLastChecked;

        RebuildRegistryClient();
        StartBackgroundRegistryProbe();
        TryLoadDjIdentity();

        configWindow = new ConfigWindow(this);
        permissionWindow = new UrlPermissionWindow(Permissions);
        setupWizard = new SetupWizardWindow(this);
        directoryWindow = new DirectoryWindow(this);
        ClubFormWindow = new ClubFormWindow(this);
        if (!Config.SetupWizardComplete) setupWizard.IsOpen = true;

        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(helpWindow);
        WindowSystem.AddWindow(permissionWindow);
        WindowSystem.AddWindow(setupWizard);
        WindowSystem.AddWindow(directoryWindow);
        WindowSystem.AddWindow(ClubFormWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/pclub play <url> | /pclub stop | /pclub calibrate <key> | /pclub config | /pclub directory",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfig;
        Framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update -= OnFrameworkUpdate;
        streamPlayer.StreamNaturallyEnded -= OnStreamNaturallyEnded;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;

        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();
        helpWindow.Dispose();
        permissionWindow.Dispose();
        setupWizard.Dispose();
        directoryWindow.Dispose();
        ClubFormWindow.Dispose();
        streamPlayer.Dispose();
#if DEBUG
        multiStreamPlayer?.Dispose();
#endif
        bgmMuter.Dispose();
        registryClient?.Dispose();
        djIdentity?.Dispose();
    }

    public void ToggleHelp() => helpWindow.Toggle();
    public void ToggleDirectory() => directoryWindow.Toggle();

    public void PlayStream(string url, ClubContext? context = null)
    {
        // No explicit context → best-effort lookup against locally-known
        // sources (saved/published houses, cached directory, cached wards).
        // A miss is fine: the prompt just doesn't show club info.
        var ctx = context ?? LookupClubContextForUrl(url);
        WithPermission(url,
            onAllow: () =>
            {
                streamPlayer.BypassSpatial();
                CurrentMode = PlaybackMode.Manual;
                userInhibitsAutoPlay = false;
                _ = StartStreamAsync(url, PlaybackMode.Manual, $"Stream: {url}");
            },
            onBlock: () => ChatGui.Print($"[ClubFFXIV] URL blocked: {url}"),
            context: ctx);
    }

    /// <summary>
    /// Run an action conditional on URL permission. Allow → onAllow now.
    /// Block → onBlock. Ask → queue prompt; onAllow runs only if user allows.
    /// Optional <paramref name="context"/> is shown on the prompt so the user
    /// can recognize "this URL belongs to club X".
    /// </summary>
    public void WithPermission(
        string url, Action onAllow, Action? onBlock = null, ClubContext? context = null)
    {
        switch (Permissions.Check(url))
        {
            case UrlDecision.Allow:
                onAllow();
                break;
            case UrlDecision.Block:
                onBlock?.Invoke();
                break;
            case UrlDecision.Ask:
                permissionWindow.Prompt(url, onAllow, onBlock ?? (() => { }), context);
                permissionWindow.EnsureOpenIfPending();
                break;
        }
    }

    private static TimeSpan BackoffForFailure(int consecutiveFailures) =>
        consecutiveFailures switch
        {
            <= 1 => TimeSpan.FromSeconds(5),
            2 => TimeSpan.FromSeconds(15),
            3 => TimeSpan.FromSeconds(60),
            _ => TimeSpan.FromMinutes(5),
        };

    /// <summary>
    /// True if <paramref name="url"/> recently failed and we're still inside
    /// its backoff window. Auto-play paths (Indoor / Outdoor) consult this
    /// before kicking off a new start so we don't retry at the framework-tick
    /// rate. Manual play deliberately bypasses the cooldown — the user's
    /// explicit click is signal that they want to try again now.
    /// </summary>
    private bool IsStreamInCooldown(string url)
    {
        lock (failureLock)
        {
            return streamFailures.TryGetValue(url, out var s)
                && DateTime.UtcNow < s.NextAttemptUtc;
        }
    }

    /// <summary>
    /// Records a failed start. Returns the (post-increment) consecutive failure
    /// count for the URL, which the caller uses to decide log severity and
    /// whether to fire the user-facing notification.
    /// </summary>
    private int RecordStreamFailure(string url, string error)
    {
        lock (failureLock)
        {
            if (!streamFailures.TryGetValue(url, out var s))
            {
                s = new StreamFailureState();
                streamFailures[url] = s;
            }
            s.ConsecutiveFailures++;
            s.LastAttemptUtc = DateTime.UtcNow;
            s.NextAttemptUtc = DateTime.UtcNow + BackoffForFailure(s.ConsecutiveFailures);
            s.LastErrorMessage = error;
            return s.ConsecutiveFailures;
        }
    }

    private void ClearStreamFailure(string url)
    {
        lock (failureLock)
        {
            streamFailures.Remove(url);
        }
    }

    /// <summary>
    /// Toast a Dalamud notification. Used for action results (publish / unpublish
    /// / rename / calibrate / etc.) where a transient toast is more discoverable
    /// than a status-line write that the user might not be looking at.
    /// </summary>
    public void Notify(string title, string content, NotificationType type, int durationSeconds = 5)
    {
        NotificationManager.AddNotification(new Notification
        {
            Title = title,
            Content = content,
            Type = type,
            InitialDuration = TimeSpan.FromSeconds(durationSeconds),
        });
    }

    /// <summary>
    /// One-shot Dalamud toast at the failure threshold. Subsequent retries on
    /// the same URL stay silent (Debug-level logs only) until the URL recovers
    /// or the user starts a new session.
    /// </summary>
    private void NotifyStreamFailed(string url, string displayName, string error)
    {
        var label = string.IsNullOrWhiteSpace(displayName) ? url : displayName;
        // Truncate URL/error to keep the toast readable; the full strings are
        // already in /xllog if a power-user wants to dig.
        var trimmedError = error.Length > 200 ? error[..197] + "..." : error;
        NotificationManager.AddNotification(new Notification
        {
            Title = "ClubFFXIV: stream unavailable",
            Content = $"{label}\n{trimmedError}\n\nWill retry every few minutes.",
            Type = NotificationType.Warning,
            InitialDuration = TimeSpan.FromSeconds(8),
        });
    }

    /// <summary>
    /// Pre-flight check for auto-play (Indoor / Outdoor): if the URL needs
    /// yt-dlp + ffmpeg but those aren't installed, emit a one-time chat
    /// warning and suppress this tick. Without this guard, the auto-play
    /// loop would re-attempt every 500ms and spam the log with bin-missing
    /// errors. Manual play already surfaces the error directly to the user
    /// via StartStreamAsync's catch.
    /// </summary>
    private bool BinariesMissingForUrl(string url)
    {
        if (UrlClassifier.ClassifyUrl(url) != AudioSourceKind.YtDlp) return false;
        if (Binaries.Ready) return false;
        if (!warnedAboutMissingBinaries)
        {
            warnedAboutMissingBinaries = true;
            ChatGui.PrintError(
                "[ClubFFXIV] A nearby club's stream needs yt-dlp + ffmpeg, which haven't " +
                "been installed yet. Open /pclub config → External binaries to download (~83 MB).");
        }
        return true;
    }

    private bool TryAutoPlayPermission(string url, ClubContext? context = null)
    {
        var decision = Permissions.Check(url);
        if (decision == UrlDecision.Allow) return true;
        if (decision == UrlDecision.Ask)
        {
            permissionWindow.Prompt(url,
                onAllow: () => { },
                onBlock: () => { },
                context: context);
            permissionWindow.EnsureOpenIfPending();
        }
        return false;
    }

    /// <summary>
    /// Best-effort URL → ClubContext lookup against everything we already know
    /// locally. Used when a manual play / paste hits the "Ask" decision and we
    /// have no caller-provided context. Returns null if no match.
    /// </summary>
    public ClubContext? LookupClubContextForUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        foreach (var entry in Config.PublishedHouses.Values)
            if (entry.StreamUrl == url) return new ClubContext(entry.DisplayName, entry.Description);
        foreach (var entry in Config.SavedHouses.Values)
            if (entry.StreamUrl == url) return new ClubContext(entry.DisplayName, entry.Description);

        if (directoryCache != null)
        {
            foreach (var c in directoryCache.Clubs)
                if (c.StreamUrl == url) return new ClubContext(c.DisplayName, c.Description);
        }

        foreach (var ward in wardCache.Values)
        {
            foreach (var c in ward.Listing.Clubs)
                if (c.StreamUrl == url) return new ClubContext(c.DisplayName, c.Description);
        }

        return null;
    }

    public void StopStream()
    {
        streamStartCts?.Cancel();
        streamPlayer.Stop();
#if DEBUG
        TearDownMultiStream();
#endif
        CurrentMode = PlaybackMode.Off;
        CurrentProximity = null;
        userInhibitsAutoPlay = true;
    }

    /// <summary>
    /// Starts a stream off the framework thread. Cancels any prior in-flight start.
    /// On completion, sets CurrentMode and prints a chat notification.
    /// </summary>
    private async Task StartStreamAsync(string url, PlaybackMode targetMode, string displayName)
    {
        // Set the dedup state synchronously — the next framework tick must see
        // pendingStartUrl set so it doesn't spawn a duplicate start.
        streamStartCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        streamStartCts = cts;
        pendingStartUrl = url;

        // Get off the framework thread before the heavy work. Without this,
        // Process.Start (yt-dlp) runs synchronously here and hitches the frame
        // by 50-200ms on Wine. Caller is `_ = StartStreamAsync(...)` from
        // OnFrameworkUpdate, so we're on the framework thread until we yield.
        await Task.Yield();

        Log.Info($"Starting stream ({targetMode}): {url}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await streamPlayer.PlayAsync(url, cts.Token);
            if (cts.IsCancellationRequested) return;
            // URL recovered — drop any prior failure state so we don't
            // keep penalizing it after a transient blip resolves.
            ClearStreamFailure(url);
            CurrentMode = targetMode;
            Log.Info($"Stream ready in {sw.ElapsedMilliseconds}ms ({targetMode}): {displayName}");
            // Only push to chat for explicit user action — auto-play (Indoor/Outdoor)
            // would spam chat every time you walk past a club.
            if (targetMode == PlaybackMode.Manual)
                ChatGui.Print($"Playing: {displayName}");
        }
        catch (OperationCanceledException)
        {
            Log.Info($"Stream start cancelled after {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            var failureCount = RecordStreamFailure(url, ex.Message);

            // Throttled logging: first few failures get a full Error stack so
            // /xllog has the diagnostic. After the threshold we drop to Debug
            // (filtered out by default) so a permanently-broken URL doesn't
            // flood the log every backoff cycle.
            if (failureCount <= NotifyAfterFailures)
                Log.Error(ex, $"Stream start failed after {sw.ElapsedMilliseconds}ms (failure #{failureCount}, {targetMode})");
            else
                Log.Debug($"Stream still failing: {url} (failure #{failureCount}, last={ex.Message})");

            if (targetMode == PlaybackMode.Manual)
            {
                // Manual play already surfaces in chat — no extra notification
                // needed. The chat error is what the user is looking at right
                // after clicking Play.
                ChatGui.PrintError($"{ex.Message}");
            }
            else if (failureCount == NotifyAfterFailures)
            {
                // Auto-play has no other UX cue. Toast exactly once when we
                // cross the threshold; subsequent retries stay silent.
                NotifyStreamFailed(url, displayName, ex.Message);
            }

            if (CurrentMode == targetMode) CurrentMode = PlaybackMode.Off;
        }
        finally
        {
            if (pendingStartUrl == url) pendingStartUrl = null;
        }
    }

    public void SetStreamVolume(float volume)
    {
        streamPlayer.MasterVolume = volume;
#if DEBUG
        if (multiStreamPlayer != null) multiStreamPlayer.MasterVolume = volume;
#endif
    }
    public bool IsStreamPlaying => streamPlayer.IsPlaying;
    public string? CurrentStreamUrl => streamPlayer.CurrentUrl;

#if DEBUG
    private bool MultiStreamActive => Config.MultiStreamEnabled;

    private MultiStreamPlayer EnsureMultiStreamPlayer()
    {
        if (multiStreamPlayer == null)
        {
            multiStreamPlayer = new MultiStreamPlayer(Binaries);
            multiStreamPlayer.MasterVolume = Config.Volume;
        }
        return multiStreamPlayer;
    }

    private void TearDownMultiStream()
    {
        multiStreamPlayer?.StopAll();
    }

    /// <summary>
    /// Called by the Advanced-tab toggle. Turning OFF tears down any active
    /// voices but keeps the WaveOutEvent around in case the user toggles back
    /// on quickly. Turning ON is a no-op until the next outdoor tick spawns
    /// the first voice (lazy by design — avoids paying the audio init cost
    /// for users who only flip the toggle to read the tooltip).
    /// </summary>
    public void OnMultiStreamToggled()
    {
        if (!Config.MultiStreamEnabled) TearDownMultiStream();
    }
#endif

    public string? DjId => djIdentity?.DjId;
    public bool RegistryEnabled => registryClient != null;

    /// <summary>
    /// Last known reachability of the configured registry. <c>null</c> = not
    /// yet probed (e.g. fresh startup, in-flight); <c>true</c> = last probe
    /// succeeded; <c>false</c> = last probe failed. Distinct from
    /// <see cref="RegistryEnabled"/>, which only reports whether a URL is set.
    /// </summary>
    public bool? RegistryConnected { get; private set; }

    public void SetRegistryConnected(bool connected) => RegistryConnected = connected;
    public void SetRegistryChecking() => RegistryConnected = null;

    /// <summary>
    /// Create or update the local-override entry for an arbitrary plot key.
    /// "Local override" = entry in <see cref="Configuration.SavedHouses"/>;
    /// it shadows the registry's record at indoor-entry lookup time.
    /// </summary>
    public void UpsertSavedHouse(string canonicalKey, string displayName, string url, string description)
    {
        if (!Config.SavedHouses.TryGetValue(canonicalKey, out var existing))
            existing = new ClubEntry();
        existing.DisplayName = displayName;
        existing.StreamUrl = url;
        existing.Description = description;
        Config.SavedHouses[canonicalKey] = existing;
        Config.Save();

        // If the active Indoor stream for this plot is now stale, switch.
        if (CurrentMode == PlaybackMode.Indoor
            && CurrentPlotKey.HasValue
            && CurrentPlotKey.Value.Canonical == canonicalKey
            && streamPlayer.CurrentUrl != url)
        {
            EnterIndoor(url, new ClubContext(displayName, description));
        }
    }

    public void DeleteSavedHouse(string canonicalKey)
    {
        if (Config.SavedHouses.Remove(canonicalKey))
            Config.Save();
    }

    /// <summary>
    /// StreamPlayer fires this when a finite source (e.g. a YouTube video)
    /// reaches a clean EOF. If the user has the loop toggle on, restart the
    /// same URL. Indefinite streams (Twitch, Icecast) never trigger this.
    /// </summary>
    private void OnStreamNaturallyEnded(string url)
    {
        if (!Config.LoopFinishedVideos) return;
        Log.Info($"Stream finished, looping: {url}");
        // PlaybackStopped fires from NAudio's audio thread; bounce to a
        // task so we don't block it on the new yt-dlp / ffmpeg startup.
        _ = Task.Run(async () =>
        {
            try { await streamPlayer.PlayAsync(url); }
            catch (Exception ex) { Log.Warning($"Auto-loop failed: {ex.Message}"); }
        });
    }

    public void RebuildRegistryClient()
    {
        registryClient?.Dispose();
        // Defensive validation: the Apply button in ConfigWindow already
        // validates, but a persisted-from-old-version or hand-edited config
        // could still hold a malformed URL. Treat invalid as disabled rather
        // than constructing a client that throws on every ward-fetch tick.
        registryClient =
            ClubRegistryClient.TryNormalizeRegistryUrl(Config.RegistryUrl, out var normalized)
            && !string.IsNullOrEmpty(normalized)
                ? new ClubRegistryClient(normalized)
                : null;
        wardCache.Clear();
        InvalidateDirectoryCache();
        RegistryConnected = null;
    }

    /// <summary>
    /// Fire-and-forget probe of the active registry client. Used on plugin
    /// startup to populate <see cref="RegistryConnected"/> without blocking;
    /// the Apply button sets that flag directly from its own probe result
    /// rather than calling this. Result is ignored if the client has been
    /// rebuilt by the time the probe completes (user reconfigured mid-flight).
    /// </summary>
    public void StartBackgroundRegistryProbe()
    {
        var client = registryClient;
        if (client == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await client.CheckHealthAsync();
                if (registryClient == client) RegistryConnected = true;
            }
            catch (Exception ex)
            {
                Log.Debug($"Background registry probe failed: {ex.Message}");
                if (registryClient == client) RegistryConnected = false;
            }
        });
    }

    public DjIdentity EnsureDjIdentity()
    {
        if (djIdentity != null) return djIdentity;
        djIdentity = DjIdentity.Generate();
        Config.DjPrivateKeyBase64 = djIdentity.ExportPrivateKeyBase64();
        Config.Save();
        return djIdentity;
    }

    /// <summary>
    /// Publish (or re-publish with full URL/desc/listed override) a club to
    /// the registry for an arbitrary plot key. The caller is responsible for
    /// gating on ownership at button-render time — typically the My Clubs
    /// "Publish new club" button only renders when the player owns the plot.
    /// Door coordinates are pulled from any existing local entry so they
    /// survive the publish.
    /// </summary>
    public async Task PublishHouseAsync(
        string canonicalKey, string displayName, string streamUrl, string description, bool listed)
    {
        if (registryClient == null)
            throw new InvalidOperationException("Registry URL not set");

        var dj = EnsureDjIdentity();

        // Pull door coords from local entry if calibrated.
        DoorPayload? door = null;
        if (Config.PublishedHouses.TryGetValue(canonicalKey, out var existing) && HasDoor(existing))
            door = ToDoorPayload(existing);
        else if (Config.SavedHouses.TryGetValue(canonicalKey, out var saved) && HasDoor(saved))
            door = ToDoorPayload(saved);

        await registryClient.PublishAsync(
            canonicalKey, streamUrl, displayName, dj, door, listed, description);

        if (existing == null)
        {
            existing = new ClubEntry();
            Config.PublishedHouses[canonicalKey] = existing;
        }
        existing.DisplayName = displayName;
        existing.StreamUrl = streamUrl;
        existing.Description = description;
        existing.Listed = listed;
        Config.Save();

        InvalidateWardCacheForDoor(door);

        // If the DJ is themselves listening in Indoor mode for this plot, the
        // active stream is stale — switch to the new URL.
        if (CurrentMode == PlaybackMode.Indoor
            && CurrentPlotKey.HasValue
            && CurrentPlotKey.Value.Canonical == canonicalKey
            && streamPlayer.CurrentUrl != streamUrl)
        {
            EnterIndoor(streamUrl, new ClubContext(displayName, description));
        }
    }

    /// <summary>
    /// Update the displayName for an already-published house. Re-publishes the
    /// existing record (same streamUrl, same door coords) signed by the DJ key
    /// — the registry's djId match is what gates this; nobody else can rename
    /// your club. Unlike <see cref="PublishHouseAsync"/>, this does NOT
    /// require the DJ to be standing inside the plot.
    /// </summary>
    public async Task RenamePublishedHouseAsync(
        string canonicalKey, string newDisplayName, string newDescription, bool newListed)
    {
        if (registryClient == null)
            throw new InvalidOperationException("Registry URL not set");
        if (djIdentity == null)
            throw new InvalidOperationException("No DJ identity — cannot rename");
        if (!Config.PublishedHouses.TryGetValue(canonicalKey, out var entry))
            throw new InvalidOperationException("House not in published list");
        if (string.IsNullOrWhiteSpace(newDisplayName))
            throw new InvalidOperationException("Name cannot be empty");
        if (newDisplayName.Length > 80)
            throw new InvalidOperationException("Name too long (max 80 chars)");
        if (newDescription.Length > 500)
            throw new InvalidOperationException("Description too long (max 500 chars)");

        var door = HasDoor(entry) ? ToDoorPayload(entry) : null;
        await registryClient.PublishAsync(
            canonicalKey, entry.StreamUrl, newDisplayName, djIdentity, door, newListed, newDescription);

        entry.DisplayName = newDisplayName;
        entry.Description = newDescription;
        entry.Listed = newListed;
        Config.Save();

        // Drop the cached ward listing so the DJ's own client refetches the
        // updated displayName for outdoor proximity (rather than serving the
        // stale name from the previous fetch for up to 60s).
        InvalidateWardCacheForDoor(door);
    }

    /// <summary>
    /// Update the displayName + description for a locally-saved house.
    /// Local-only; no network.
    /// </summary>
    public void RenameSavedHouse(string canonicalKey, string newDisplayName, string newDescription)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName)) return;
        if (!Config.SavedHouses.TryGetValue(canonicalKey, out var entry)) return;
        entry.DisplayName = newDisplayName.Length > 80 ? newDisplayName[..80] : newDisplayName;
        entry.Description = newDescription.Length > 500 ? newDescription[..500] : newDescription;
        Config.Save();
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
            ChatGui.PrintError("Calibrate: must be standing in an outdoor ward.");
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
                var desc = pub.Description;
                var url = pub.StreamUrl;
                var listed = pub.Listed;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await registryClient.PublishAsync(
                            canonicalKey, url, dn, djIdentity, doorPayload, listed, desc);
                        InvalidateWardCacheForDoor(doorPayload);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Auto-republish after calibrate failed: {ex.Message}");
                    }
                });
            }
        }

        if (!wrote)
        {
            ChatGui.PrintError($"Calibrate: no house with key {canonicalKey}");
            return false;
        }

        Config.Save();
        ChatGui.Print($"Door calibrated at ({pos.Value.X:F1}, {pos.Value.Y:F1}, {pos.Value.Z:F1})");
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

        // Per-step timing so we can pinpoint hitches (Dalamud warns at >50ms).
        // Logs only on slow ticks to keep /xllog quiet during normal operation.
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        long locMs = 0, driveMs = 0, policyMs = 0, binMs = 0;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            UpdateLocationState();
            locMs = sw.ElapsedMilliseconds; sw.Restart();

            DriveAudio();
            driveMs = sw.ElapsedMilliseconds; sw.Restart();

            ApplyAudioPolicy();
            policyMs = sw.ElapsedMilliseconds; sw.Restart();

            MaybeCheckBinaryUpdates();
            binMs = sw.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OnFrameworkUpdate failed");
        }
        finally
        {
            var totalMs = totalSw.ElapsedMilliseconds;
            if (totalMs > 30)
            {
                Log.Warning(
                    $"Slow tick: {totalMs}ms total " +
                    $"(loc={locMs} drive={driveMs} policy={policyMs} bin={binMs})");
            }
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
        // No binaries installed = nothing to update. The user has to opt into
        // installation explicitly (setup wizard or /pclub config); we don't
        // download in the background.
        if (!Binaries.Ready) return;
        if (DateTime.UtcNow - lastBinaryUpdateCheck < BinaryUpdateInterval) return;

        lastBinaryUpdateCheck = DateTime.UtcNow;
        Config.BinariesLastChecked = DateTime.UtcNow;
        Config.Save();

        _ = Task.Run(async () =>
        {
            try
            {
                var newVersion = await Binaries.UpdateYtDlpAsync();
                Log.Info($"yt-dlp now at: {newVersion}");
            }
            catch (Exception ex)
            {
                Log.Warning($"Binary update check failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// After audio state is settled for the tick, apply cross-cutting policies:
    /// game BGM muting (when our stream plays) and focus muting (when game unfocused).
    /// </summary>
    /// <summary>
    /// Force the next framework tick to refresh housing state immediately rather
    /// than waiting up to 500ms. Called on territory change so BGM mute / auto-play
    /// kicks in within ~1 frame of crossing the instance boundary instead of
    /// half a second later.
    /// </summary>
    private void OnTerritoryChanged(uint territoryType)
    {
        lastHousingCheck = DateTime.MinValue;
    }

    private void ApplyAudioPolicy()
    {
        // Stream output mute when game is unfocused.
        var shouldMute = Config.MuteStreamWhenUnfocused && !WindowFocus.IsGameFocused();
        streamPlayer.Muted = shouldMute;
#if DEBUG
        if (multiStreamPlayer != null) multiStreamPlayer.Muted = shouldMute;
#endif

        // Game BGM mute only when stream is the primary audio source (indoor / manual).
        // Outdoor proximity is meant to layer *over* the world's own BGM, not replace it.
        // Mute while a load is pending too — otherwise the game's BGM blares for the
        // 1–3s the stream takes to connect after entering a house.
        var streamWillPlay = streamPlayer.IsPlaying || pendingStartUrl != null;
#if DEBUG
        if (!streamWillPlay && MultiStreamActive && multiStreamPlayer?.HasAnyActivity == true)
            streamWillPlay = true;
#endif
        var streamIsPrimary = streamWillPlay
            && CurrentMode is PlaybackMode.Indoor or PlaybackMode.Manual;
        if (streamIsPrimary)
            bgmMuter.Mute();
        else
            bgmMuter.Unmute();
    }

    private void UpdateLocationState()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var newPlot = HousingDetector.ResolveCurrent(Config.KeepPlayingInLinkedSubterritories);
        var resolveCurrentMs = sw.ElapsedMilliseconds; sw.Restart();

        var newWard = HousingDetector.ResolveOutdoor();
        var resolveOutdoorMs = sw.ElapsedMilliseconds; sw.Restart();

        if (!Nullable.Equals(newPlot, CurrentPlotKey))
        {
            CurrentPlotKey = newPlot;
            // Territory change resets the user-stop suppression — auto-play is fair
            // game again in the new location.
            userInhibitsAutoPlay = false;
        }

        if (!Nullable.Equals(newWard, CurrentWard))
        {
            CurrentWard = newWard;
            userInhibitsAutoPlay = false;
        }

        // Cache ownership while we're on the framework thread — publish flow
        // runs in a Task.Run continuation and can't safely call CheckOwnership.
        CurrentOwnership = newPlot.HasValue
            ? HousingDetector.CheckOwnership()
            : HouseOwnership.Unknown;
        var ownershipMs = sw.ElapsedMilliseconds; sw.Restart();

        // Always make sure we have a fresh listing for the current ward.
        // EnsureWardListingAsync is a no-op when cache is fresh or a fetch is in flight,
        // so calling it every tick is free *as long as it doesn't block the framework
        // thread*. HttpClient.GetAsync inside has a sync prefix (DNS, connection pool
        // lookup) that can stall under Wine — wrap in Task.Run to push the whole
        // chain onto the threadpool.
        if (CurrentWard.HasValue && registryClient != null)
        {
            var ward = CurrentWard.Value;
            _ = Task.Run(() => EnsureWardListingAsync(ward));
        }
        var wardFetchKickoffMs = sw.ElapsedMilliseconds;

        var totalMs = resolveCurrentMs + resolveOutdoorMs + ownershipMs + wardFetchKickoffMs;
        if (totalMs > 20)
        {
            Log.Warning(
                $"Slow loc: {totalMs}ms " +
                $"(resolveIndoor={resolveCurrentMs} resolveOutdoor={resolveOutdoorMs} " +
                $"ownership={ownershipMs} wardKickoff={wardFetchKickoffMs})");
        }
    }

    private void DriveAudio()
    {
        // Manual playback is sticky — never override with auto-play.
        if (CurrentMode == PlaybackMode.Manual && (streamPlayer.IsPlaying || pendingStartUrl != null))
            return;

        // User explicitly stopped — don't auto-resume until they Play again or
        // change territory.
        if (userInhibitsAutoPlay) return;

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
#if DEBUG
            TearDownMultiStream();
#endif
            CurrentMode = PlaybackMode.Off;
            CurrentProximity = null;
        }
    }

    private void HandleIndoorMode(PlotKey key)
    {
        bool alreadySettled;
#if DEBUG
        if (MultiStreamActive)
        {
            // Multi-stream Indoor: voice exists for this plot ⇒ already settled.
            // Re-apply bypass each tick to cover the case where AddVoiceAsync
            // just completed at an outdoor cutoff (transition is multi-tick).
            alreadySettled = CurrentMode == PlaybackMode.Indoor
                && multiStreamPlayer != null
                && multiStreamPlayer.HasVoice(key.Canonical);
            if (alreadySettled)
            {
                multiStreamPlayer!.SetSpatial(key.Canonical, 1.0f, MultiStreamPlayer.BypassCutoffHz);
            }
        }
        else
        {
            alreadySettled = CurrentMode == PlaybackMode.Indoor && streamPlayer.IsPlaying;
        }
#else
        alreadySettled = CurrentMode == PlaybackMode.Indoor && streamPlayer.IsPlaying;
#endif

        if (alreadySettled)
        {
            CurrentProximity = null;
            return;
        }

        if (Config.SavedHouses.TryGetValue(key.Canonical, out var saved)
            && !string.IsNullOrWhiteSpace(saved.StreamUrl))
        {
            EnterIndoor(saved.StreamUrl, new ClubContext(saved.DisplayName, saved.Description));
            return;
        }

        if (registryClient != null)
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
            EnterIndoor(record.StreamUrl, new ClubContext(record.DisplayName, record.Description));
        }
        catch (Exception ex)
        {
            Log.Warning($"Registry lookup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Enter indoor playback for the given URL. If we were already playing this exact
    /// URL outdoors (player walked from the door into the house), drop the spatial
    /// chain in place — no restart, no rebuffering.
    /// </summary>
    private void EnterIndoor(string url, ClubContext context)
    {
#if DEBUG
        if (MultiStreamActive)
        {
            EnterIndoorMulti(url, context);
            return;
        }
#endif

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

        if (!TryAutoPlayPermission(url, context)) return;
        if (BinariesMissingForUrl(url)) return;
        if (IsStreamInCooldown(url)) return;

        streamPlayer.BypassSpatial();
        // Optimistic: claim Indoor mode now so ApplyAudioPolicy mutes the game's
        // BGM immediately, even though the stream chain takes a moment to load.
        // StartStreamAsync reverts to Off on load failure.
        CurrentMode = PlaybackMode.Indoor;
        _ = StartStreamAsync(url, PlaybackMode.Indoor, context.ClubName);
    }

#if DEBUG
    /// <summary>
    /// Indoor entry in multi-stream mode. Indoor is solo: keep only the voice
    /// for the current plot (so the user hears one club distinctly), and bypass
    /// its spatial filter (full volume, full bandwidth). If the voice was
    /// already streaming outdoors, this is a true seamless transition — no
    /// reconnect, no rebuffering. If not, kick off a fresh AddVoiceAsync with
    /// bypass params; HandleIndoorMode's per-tick re-apply keeps the bypass in
    /// place as the voice finishes loading.
    /// </summary>
    private void EnterIndoorMulti(string url, ClubContext context)
    {
        // Single-voice player is unused while multi-stream owns the output.
        if (streamPlayer.IsPlaying) streamPlayer.Stop();

        var canonical = CurrentPlotKey?.Canonical;
        if (canonical == null) return;

        var player = EnsureMultiStreamPlayer();

        // Solo: drop every voice that isn't this plot's.
        foreach (var key in player.ActiveKeys())
        {
            if (key != canonical) player.RemoveVoice(key);
        }

        if (player.HasVoice(canonical))
        {
            // Seamless reuse — the outdoor voice stays alive, just unmuffled.
            player.SetSpatial(canonical, 1.0f, MultiStreamPlayer.BypassCutoffHz);
            CurrentMode = PlaybackMode.Indoor;
            CurrentProximity = null;
            return;
        }

        if (player.IsStarting(canonical))
        {
            // Voice is already mid-startup from the outdoor path. Don't restart;
            // HandleIndoorMode's idempotent re-apply will swap it to bypass on
            // the tick after AddVoiceAsync completes.
            CurrentMode = PlaybackMode.Indoor;
            CurrentProximity = null;
            return;
        }

        if (!TryAutoPlayPermission(url, context)) return;
        if (BinariesMissingForUrl(url)) return;
        if (IsStreamInCooldown(url)) return;

        // Optimistic mode flip — same rationale as the single-stream path.
        CurrentMode = PlaybackMode.Indoor;
        CurrentProximity = null;
        _ = player.AddVoiceAsync(canonical, url, MultiStreamPlayer.BypassCutoffHz);
    }
#endif

    private void HandleOutdoorMode(WardLocation ward)
    {
        var pos = HousingDetector.PlayerPosition();
        if (pos == null) return;

#if DEBUG
        if (MultiStreamActive)
        {
            HandleOutdoorModeMulti(ward, pos.Value);
            return;
        }
#endif

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
            var ctx = new ClubContext(r.Candidate.DisplayName, r.Candidate.Description);
            if (!TryAutoPlayPermission(r.Candidate.StreamUrl, ctx)) return;
            if (BinariesMissingForUrl(r.Candidate.StreamUrl)) return;
            if (IsStreamInCooldown(r.Candidate.StreamUrl)) return;

            Log.Info(
                $"Outdoor restart trigger: " +
                $"currentUrlMatches={streamPlayer.CurrentUrl == r.Candidate.StreamUrl} " +
                $"mode={CurrentMode} isPlaying={streamPlayer.IsPlaying}");
            _ = StartStreamAsync(r.Candidate.StreamUrl, PlaybackMode.Outdoor, r.Candidate.DisplayName);
        }
        else if (CurrentMode != PlaybackMode.Outdoor && streamPlayer.IsPlaying)
        {
            CurrentMode = PlaybackMode.Outdoor;
        }
    }

#if DEBUG
    /// <summary>
    /// Outdoor proximity loop for multi-stream mode. Diffs the desired voice
    /// set (in-range candidates, capped) against the active mixer voices:
    /// remove voices for plots out of range or evicted by the cap, add voices
    /// for newly-in-range plots, and update spatial params on still-active
    /// voices. yt-dlp candidates count as 2 against MaxConcurrentStreams.
    /// </summary>
    private void HandleOutdoorModeMulti(WardLocation ward, System.Numerics.Vector3 pos)
    {
        var candidates = EnumerateCandidates(ward);
        var allInRange = WardProximity.FindAllInRange(
            pos,
            candidates,
            Config.SpatialStreamDistance,
            Config.SpatialFalloffDistance,
            Config.SpatialFullVolumeDistance);

        // UI proximity readout shows the closest club, regardless of whether
        // it ended up with a voice (e.g. cap might have evicted it — unusual,
        // but possible if a much closer non-yt-dlp later appears).
        CurrentProximity = allInRange.Count > 0 ? allInRange[0] : (WardProximity.Result?)null;

        var player = multiStreamPlayer;
        if (allInRange.Count == 0)
        {
            if (CurrentMode == PlaybackMode.Outdoor)
            {
                if (player != null) TearDownMultiStream();
                CurrentMode = PlaybackMode.Off;
            }
            return;
        }

        // Lazy-init: only build the WaveOutEvent on first actually-in-range tick.
        player ??= EnsureMultiStreamPlayer();

        // The single-voice player may still be playing from a prior Indoor or
        // Manual session; outdoor multi-stream mode owns the audio output, so
        // stop the single player before voicing anything new.
        if (streamPlayer.IsPlaying) streamPlayer.Stop();

        // Build desired set under the cap. yt-dlp = 2 voice-units (each spawns
        // yt-dlp + ffmpeg subprocesses), direct HTTP = 1.
        var max = Math.Clamp(Config.MaxConcurrentStreams, 1, 10);
        var desired = new List<WardProximity.Result>();
        var desiredKeys = new HashSet<string>();
        var cost = 0;
        foreach (var r in allInRange)
        {
            var voiceCost = UrlClassifier.ClassifyUrl(r.Candidate.StreamUrl) == AudioSourceKind.YtDlp ? 2 : 1;
            if (cost + voiceCost > max) continue;
            desired.Add(r);
            desiredKeys.Add(r.Candidate.CanonicalKey);
            cost += voiceCost;
        }

        foreach (var key in player.ActiveKeys())
        {
            if (!desiredKeys.Contains(key)) player.RemoveVoice(key);
        }

        foreach (var r in desired)
        {
            var url = r.Candidate.StreamUrl;
            var key = r.Candidate.CanonicalKey;
            var cutoff = WardProximity.NearnessToCutoff(
                r.NormalizedNearness,
                Config.SpatialMinCutoffHz,
                Config.SpatialMaxCutoffHz);

            if (player.HasVoice(key))
            {
                player.SetSpatial(key, r.NormalizedNearness, cutoff);
                continue;
            }
            if (player.IsStarting(key)) continue;

            var ctx = new ClubContext(r.Candidate.DisplayName, r.Candidate.Description);
            if (!TryAutoPlayPermission(url, ctx)) continue;
            if (BinariesMissingForUrl(url)) continue;
            if (IsStreamInCooldown(url)) continue;

            _ = player.AddVoiceAsync(key, url, cutoff);
        }

        if (player.VoiceCount > 0 && CurrentMode != PlaybackMode.Outdoor)
        {
            CurrentMode = PlaybackMode.Outdoor;
        }
    }
#endif

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
                    new Vector3(entry.Door.X, entry.Door.Y, entry.Door.Z),
                    entry.Description);
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
            key, entry.DisplayName, entry.StreamUrl, entry.DoorPosition.ToVec(), entry.Description);
        return true;
    }

    // ---- Ward listing cache ----

    private bool TryGetCachedWard(WardLocation ward, out WardListing listing)
    {
        // Serve cached data regardless of TTL age. The TTL only governs whether
        // EnsureWardListingAsync kicks off a refetch — never whether we serve.
        // Otherwise we'd briefly drop all registry candidates on TTL expiry,
        // causing HandleOutdoorMode to Stop() the stream and trigger a spurious
        // restart once the refetch completed.
        var k = WardCacheKey(ward);
        if (wardCache.TryGetValue(k, out var entry))
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
            Log.Info($"Fetching ward listing: world={worldId} territory={ward.TerritoryType} ward={ward.Ward}");
            var listing = await registryClient.GetWardAsync(worldId, ward.TerritoryType, ward.Ward);
            wardCache[k] = new CachedWardListing(DateTime.UtcNow, listing);
            Log.Info($"Ward listing fetched: {listing.Clubs.Count} club(s)");
        }
        catch (Exception ex)
        {
            // Use Log.Error so the inner exception chain is fully serialized.
            Log.Error(ex, "Ward listing fetch failed");
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

    // ---- Public directory cache ----

    /// <summary>Last fetched directory listing, if any. UI thread reads this.</summary>
    public DirectoryListing? DirectoryCache => directoryCache;

    public DateTime DirectoryCacheFetchedAt => directoryCacheFetchedAt;

    public bool DirectoryFetchInFlight => directoryFetchInFlight;

    /// <summary>
    /// Fetches the public directory listing. Serves cached data when fresh,
    /// otherwise hits the registry. <paramref name="force"/> bypasses the TTL
    /// (used by the manual Refresh button). Concurrent calls collapse to the
    /// in-flight fetch.
    /// </summary>
    public async Task<DirectoryListing> FetchDirectoryAsync(bool force = false)
    {
        if (registryClient == null)
            throw new InvalidOperationException("Registry URL not set");

        if (!force
            && directoryCache != null
            && DateTime.UtcNow - directoryCacheFetchedAt < DirectoryCacheTtl)
        {
            return directoryCache;
        }

        if (directoryFetchInFlight && directoryCache != null)
            return directoryCache;

        directoryFetchInFlight = true;
        try
        {
            var listing = await registryClient.GetDirectoryAsync();
            directoryCache = listing;
            directoryCacheFetchedAt = DateTime.UtcNow;
            return listing;
        }
        finally
        {
            directoryFetchInFlight = false;
        }
    }

    /// <summary>
    /// Drop cached directory data entirely. Use only when the data source
    /// itself changed (e.g. registry URL reconfiguration); the old rows are
    /// no longer meaningful in the new context.
    /// </summary>
    private void InvalidateDirectoryCache()
    {
        directoryCache = null;
        directoryCacheFetchedAt = DateTime.MinValue;
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
                    ChatGui.Print("Usage: /pclub play <stream-url>");
                    return;
                }
                try
                {
                    Config.LastStreamUrl = rest;
                    Config.Save();
                    PlayStream(rest);
                    ChatGui.Print($"Playing {rest}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to start stream");
                    ChatGui.PrintError($"{ex.Message}");
                }
                break;

            case "stop":
                StopStream();
                ChatGui.Print("Stopped");
                break;

            case "calibrate":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    ChatGui.Print("Usage: /pclub calibrate <plotKey>");
                    return;
                }
                CalibrateDoor(rest);
                break;

            case "config":
                OpenConfig();
                break;

            case "directory":
            case "browse":
                ToggleDirectory();
                break;

            default:
                ChatGui.Print("Usage: /pclub play <url> | /pclub stop | /pclub calibrate <key> | /pclub config | /pclub directory");
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
