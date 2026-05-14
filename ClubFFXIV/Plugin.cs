using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using ClubFFXIV.Audio;
using ClubFFXIV.Game;
using ClubFFXIV.Network;
using ClubFFXIV.UI;
using Dalamud.Game.Command;
using Dalamud.Game.Config;
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
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

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

    // Three top-level listener surfaces: the music player is the primary
    // (Now Playing rows + URL input + body controls); MiniPlayerWindow is
    // the same Now Playing strip without body chrome, for ambient
    // listening; ConfigWindow is settings-only, reachable via the music
    // player's title-bar gear or via "/pclub config".
    private readonly MusicPlayerWindow musicPlayerWindow;
    private readonly MiniPlayerWindow miniPlayerWindow;
    private readonly ConfigWindow configWindow;
    private readonly HelpWindow helpWindow = new();
    private readonly UrlPermissionWindow permissionWindow;
    private readonly ClubPassphrasePromptWindow passphrasePromptWindow;
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
    // Lazily created: the WaveOutEvent inside MultiStreamPlayer is non-trivial,
    // so we only build it once the user opts in via the Advanced tab toggle.
    private MultiStreamPlayer? multiStreamPlayer;
    private readonly GameBgmMuter bgmMuter = new();
    private ClubRegistryClient? registryClient;
    private DateTime lastBinaryUpdateCheck = DateTime.MinValue;
    private static readonly TimeSpan BinaryUpdateInterval = TimeSpan.FromDays(2);
    private DjIdentity? djIdentity;
    private DateTime lastHousingCheck = DateTime.MinValue;
    private static readonly TimeSpan HousingCheckInterval = TimeSpan.FromMilliseconds(500);

    // Cached door positions for currently-active outdoor voices. Populated by
    // the proximity tick (HandleOutdoorMode/Multi); read by RefreshSpatialPose
    // every framework frame to update pan + cutoff without re-running the
    // full candidate sweep. The proximity tick stays at HousingCheckInterval
    // (heavy: dict iteration, registry filter, voice add/remove) but the
    // directional cue follows the camera at frame rate.
    private readonly Dictionary<string, Vector3> activeMultiDoorPos = new();
    private Vector3? activeSingleDoorPos;

    // Multi-stream voices that just naturally ended while LoopFinishedVideos
    // was off. Suppresses HandleOutdoorModeMulti from re-adding them on the
    // next 500ms tick — the proximity loop only sees "candidate in range, no
    // voice exists" and races past the Loop gate in
    // OnMultiStreamVoiceNaturallyEnded. Cleared when the user walks out of
    // the candidate's range, changes territory, or stops/replays the stream.
    private readonly HashSet<string> recentlyEndedMultiKeys = new();

    // Focus-mute policy state. Both values are cached so we never read them
    // on the hot path: soundsPlayWhenInactive is fed by IGameConfig.SystemChanged
    // and persisted in Config so alt-tab muting works at startup before
    // GameConfig becomes readable; gameFocused is fed by per-tick edge
    // detection in OnFrameworkUpdate. The mute is recomputed (and AutoMuted
    // written) only when one of them actually transitions, so quiet ticks
    // do nothing.
    //
    // The plugin's stream is meant to feel diegetic — like the music belongs to
    // the game world — so it follows FFXIV's own "Play sounds when window is
    // not active" setting rather than carrying a redundant toggle of its own.
    private bool soundsPlayWhenInactive = true;
    private bool gameFocused = true;
    // Once true, we've successfully read both system options at least once
    // this session; OnFrameworkUpdate stops retrying. Without this, an early
    // plugin load (title screen, before login) leaves Config as the only
    // source of truth — fine across sessions, but if the user changed the
    // FFXIV setting outside the game we'd never pick it up otherwise.
    private bool soundsPlayWhenInactiveSeeded;

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

    // Resolved-URL cache for password-protected clubs the listener has
    // authenticated against. Populated opportunistically on indoor-prompt
    // success and proactively after each ward fetch (only for plots with a
    // cached passphrase). EnumerateCandidates substitutes from this cache so
    // outdoor proximity can play locked clubs without re-prompting. In-memory
    // only — re-derived on each session from KnownClubPassphrases + a fresh
    // registry round-trip.
    private readonly Dictionary<string, string> resolvedPasswordedUrls = new();
    private readonly HashSet<string> resolvingPasswordedUrls = new();
    // Plots the user dismissed via Skip on the passphrase prompt. Cleared on
    // territory change so leaving and re-entering the plot re-prompts.
    private readonly HashSet<string> skippedPasswordedPlots = new();

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

        // Seed the listener-side passphrase + URL caches from PublishedHouses
        // entries that already carry a Passphrase. Covers two cases that the
        // publish-side auto-populate misses: (1) clubs whose passphrase was
        // set before the auto-populate code landed, (2) plugin restarts —
        // resolvedPasswordedUrls is in-memory only and would be empty
        // otherwise, so the DJ's own outdoor mode would have to wait for the
        // ward warm-fetch round-trip on every session.
        foreach (var (key, entry) in Config.PublishedHouses)
        {
            if (string.IsNullOrEmpty(entry.Passphrase)) continue;
            Config.KnownClubPassphrases[key] = entry.Passphrase;
            if (!string.IsNullOrEmpty(entry.StreamUrl))
                resolvedPasswordedUrls[key] = entry.StreamUrl;
        }

        Binaries = new BinaryManager(PluginInterface.GetPluginConfigDirectory());
        Permissions = new UrlPermissions(Config);
        streamPlayer = new StreamPlayer(Binaries);
        streamPlayer.MasterVolume = Config.Volume;
        streamPlayer.PlaylistRandom = Config.PlaylistRandom;
        streamPlayer.YtDlpCookiesBrowser = Config.YtDlpCookiesBrowser;
        streamPlayer.StreamNaturallyEnded += OnStreamNaturallyEnded;
        lastBinaryUpdateCheck = Config.BinariesLastChecked;

        RebuildRegistryClient();
        StartBackgroundRegistryProbe();
        TryLoadDjIdentity();

        musicPlayerWindow = new MusicPlayerWindow(this);
        miniPlayerWindow = new MiniPlayerWindow(this);
        configWindow = new ConfigWindow(this);
        permissionWindow = new UrlPermissionWindow(Permissions);
        passphrasePromptWindow = new ClubPassphrasePromptWindow();
        setupWizard = new SetupWizardWindow(this);
        directoryWindow = new DirectoryWindow(this);
        ClubFormWindow = new ClubFormWindow(this);
        if (!Config.SetupWizardComplete) setupWizard.IsOpen = true;

        WindowSystem.AddWindow(musicPlayerWindow);
        WindowSystem.AddWindow(miniPlayerWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(helpWindow);
        WindowSystem.AddWindow(permissionWindow);
        WindowSystem.AddWindow(passphrasePromptWindow);
        WindowSystem.AddWindow(setupWizard);
        WindowSystem.AddWindow(directoryWindow);
        WindowSystem.AddWindow(ClubFormWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/pclub (open music player) | mini | play <url> | stop | calibrate <key> | config | directory",
        });

        // Seed from the persisted last-known value so alt-tab muting is
        // correct even if GameConfig isn't readable yet (early plugin load,
        // title screen). TryRefresh… below promotes to the live value when
        // available; if it fails, OnFrameworkUpdate keeps retrying until
        // the option becomes readable post-login.
        soundsPlayWhenInactive = Config.SoundsPlayWhenInactive;
        gameFocused = WindowFocus.IsGameFocused();
        GameConfig.SystemChanged += OnSystemConfigChanged;
        TryRefreshSoundsPlayWhenInactiveFromGameConfig();
        // Apply the seeded policy to streamPlayer now — RecomputeFocusMute is
        // edge-driven (only fires on transitions), so without this a plugin
        // loaded while the user is alt-tabbed would leave AutoMuted=false on
        // streamPlayer until the first focus toggle.
        RecomputeFocusMute();

        PluginInterface.UiBuilder.Draw += DrawUI;
        // Dalamud routing convention: OpenMainUi → primary user surface
        // (the music player); OpenConfigUi → settings (the gear icon in
        // the Plugin Installer maps here). The music player itself carries
        // a gear button that toggles the same settings window.
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenMusicPlayer;
        Framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update -= OnFrameworkUpdate;
        GameConfig.SystemChanged -= OnSystemConfigChanged;
        streamPlayer.StreamNaturallyEnded -= OnStreamNaturallyEnded;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMusicPlayer;

        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        musicPlayerWindow.Dispose();
        miniPlayerWindow.Dispose();
        configWindow.Dispose();
        helpWindow.Dispose();
        permissionWindow.Dispose();
        passphrasePromptWindow.Dispose();
        setupWizard.Dispose();
        directoryWindow.Dispose();
        ClubFormWindow.Dispose();
        streamPlayer.Dispose();
        multiStreamPlayer?.Dispose();
        bgmMuter.Dispose();
        registryClient?.Dispose();
        djIdentity?.Dispose();
        UI.NowPlayingThumbnails.DisposeAll();
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
                // Manual play is "this is the only thing I want to hear":
                // evict any proximity-discovered multi-stream voices so they
                // don't keep mixing in alongside the user's chosen URL. The
                // CurrentMode = Manual flag below prevents the next outdoor
                // tick from re-adding them.
                TearDownMultiStream();
                streamPlayer.BypassSpatial();
                CurrentMode = PlaybackMode.Manual;
                userInhibitsAutoPlay = false;
                _ = StartStreamAsync(url, PlaybackMode.Manual, $"Stream: {url}");
            },
            onBlock: () => Notify("ClubFFXIV: URL blocked", url, NotificationType.Warning),
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
    /// yt-dlp + ffmpeg + Deno but those aren't installed, emit a one-time
    /// toast and suppress this tick. Without this guard, the auto-play
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
            Notify(
                "ClubFFXIV: missing binaries",
                "A nearby club's stream needs yt-dlp + ffmpeg + Deno, which haven't been installed yet. " +
                "Open /pclub config → External binaries to download (~123 MB).",
                NotificationType.Warning,
                durationSeconds: 10);
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
        TearDownMultiStream();
        CurrentMode = PlaybackMode.Off;
        CurrentProximity = null;
        userInhibitsAutoPlay = true;
    }

    /// <summary>
    /// Starts a stream off the framework thread. Cancels any prior in-flight start.
    /// On completion, sets CurrentMode and toasts a notification.
    /// <paramref name="announce"/> = false suppresses toasts for auto-play
    /// (Indoor/Outdoor are gated by targetMode; the loop path is also auto and
    /// passes false to skip the per-iteration "Playing:" toast).
    /// </summary>
    private async Task StartStreamAsync(string url, PlaybackMode targetMode, string displayName, bool announce = true)
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
            // Only toast for explicit user action — auto-play (Indoor/Outdoor)
            // would spam notifications every time you walk past a club. Loop
            // iterations pass announce=false for the same reason.
            if (targetMode == PlaybackMode.Manual && announce)
            {
                Notify("ClubFFXIV", $"Playing: {displayName}", NotificationType.Info);
                // Same gate as the toast — auto-open the mini only for explicit
                // manual starts, not for loop iterations or auto-play. IsOpen
                // is a plain bool on the Dalamud Window; the WindowSystem reads
                // it on the framework thread next frame, so a cross-thread set
                // from this async continuation is fine.
                if (Config.OpenMiniPlayerOnManualStart)
                {
                    musicPlayerWindow.IsOpen = false;
                    miniPlayerWindow.IsOpen = true;
                }
            }
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

            if (targetMode == PlaybackMode.Manual && announce)
            {
                // Manual play surfaces the failure as an error toast right after
                // the user clicked Play, so they see *why* nothing started.
                // Loop iterations skip this so a transient failure mid-loop
                // doesn't spam.
                Notify("ClubFFXIV: stream failed", ex.Message, NotificationType.Error);
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
        if (multiStreamPlayer != null) multiStreamPlayer.MasterVolume = volume;
    }

    /// <summary>
    /// One row in the Now Playing header. Abstracts over single-stream and
    /// per-voice multi-stream so the UI can render uniform rows. PlotKey is
    /// null for the single-stream row, set for multi-stream voices.
    /// ThumbnailUrl is null for sources that don't expose artwork (Icecast,
    /// raw HTTP MP3) or before yt-dlp's first --print line lands; the UI
    /// falls back to a placeholder square in that case. CropThumbToSquare
    /// is true when the source is known to pad square content into a wider
    /// frame (YouTube Music / Topic channels) — the renderer cover-crops
    /// the centre square instead of stretching the full image.
    ///
    /// Transport fields (IsLive / IsPaused / Seekable / PositionSeconds /
    /// DurationSeconds) describe the source's playback shape. Live rows
    /// keep the existing mute-only chrome — pausing or seeking a live
    /// stream has no useful semantic. Non-live + Seekable rows render
    /// play/pause + a seek slider; non-live but not Seekable (reserved for
    /// future HTTP-Range MP3 support) renders play/pause + timestamp only.
    /// </summary>
    public readonly record struct NowPlayingEntry(
        string Url,
        string Label,
        bool Muted,
        string? PlotKey,
        string? ThumbnailUrl,
        bool CropThumbToSquare,
        bool IsLive,
        bool IsPaused,
        bool Seekable,
        bool CanSkipNext,
        double PositionSeconds,
        double DurationSeconds);

    /// <summary>
    /// Snapshot of every audio source currently producing (or about to produce)
    /// sound. Empty list = "Not playing". Single-stream and multi-stream voices
    /// are interleaved; the UI renders one row per entry. Label prefers the
    /// resolved title (yt-dlp's --print title for Yt/Twitch, icy-name +
    /// inline StreamTitle for Icecast) and falls back to a truncated URL when
    /// the title isn't (yet) known.
    /// </summary>
    public IReadOnlyList<NowPlayingEntry> GetNowPlayingEntries()
    {
        var result = new List<NowPlayingEntry>();

        // IsActive (Playing OR Paused) instead of IsPlaying alone — a paused
        // primary stream must stay in the Now Playing list, otherwise the
        // user has no way to click resume.
        if (streamPlayer.IsActive && streamPlayer.CurrentUrl is { Length: > 0 } url)
        {
            var thumb = ThumbnailCache.Get(url);
            var isLive = streamPlayer.IsLive;
            result.Add(new NowPlayingEntry(
                url, GetDisplayLabel(url), streamPlayer.UserMuted,
                PlotKey: null,
                ThumbnailUrl: thumb?.Url,
                CropThumbToSquare: thumb?.CropToSquare ?? false,
                IsLive: isLive,
                IsPaused: streamPlayer.IsPaused,
                // Seekable mirrors "non-live and source supports it". For now
                // that's only ffmpeg-backed sources (yt-dlp), gated by IsLive.
                // PositionSeconds returns 0 when the source isn't seekable, so
                // hiding the seek bar via this flag is sufficient.
                Seekable: !isLive && streamPlayer.PositionSeconds >= 0
                          && streamPlayer.DurationSeconds > 0,
                // Skip-next only when the source is actually a multi-item
                // playlist. yt-dlp's playlist_count metadata is 0 for single
                // videos (and unset for video-with-playlist URLs we resolve
                // as the video via --no-playlist), so the icon stays hidden
                // there. Without this, single videos got a misleading skip
                // affordance that just looped/stopped the same track.
                CanSkipNext: !isLive && streamPlayer.IsSkippable
                             && PlaylistCountCache.Get(url) > 1,
                PositionSeconds: streamPlayer.PositionSeconds,
                DurationSeconds: streamPlayer.DurationSeconds));
        }

        if (multiStreamPlayer is { HasAnyActivity: true } msp)
        {
            foreach (var key in msp.ActiveKeys())
            {
                var voiceUrl = msp.GetVoiceUrl(key) ?? "";
                var voiceThumb = ThumbnailCache.Get(voiceUrl);
                var voiceLive = voiceUrl.Length == 0 || LiveStatusCache.IsLive(voiceUrl);
                var voiceDuration = voiceUrl.Length > 0 ? DurationCache.Get(voiceUrl) : 0;
                result.Add(new NowPlayingEntry(
                    voiceUrl, GetDisplayLabel(voiceUrl), msp.IsVoiceMuted(key),
                    PlotKey: key,
                    ThumbnailUrl: voiceThumb?.Url,
                    CropThumbToSquare: voiceThumb?.CropToSquare ?? false,
                    IsLive: voiceLive,
                    IsPaused: msp.IsVoicePaused(key),
                    Seekable: !voiceLive && msp.IsVoiceSeekable(key) && voiceDuration > 0,
                    CanSkipNext: !voiceLive && msp.IsVoiceSkippable(key)
                                 && PlaylistCountCache.Get(voiceUrl) > 1,
                    PositionSeconds: msp.GetVoicePosition(key),
                    DurationSeconds: voiceDuration));
            }
        }

        return result;
    }

    /// <summary>Human-readable label for a stream URL. Title if cached, else truncated URL.</summary>
    public string GetDisplayLabel(string url) =>
        TitleCache.Get(url) ?? TruncateUrl(url);

    private static string TruncateUrl(string url) =>
        url.Length > 60 ? url[..57] + "..." : url;

    /// <summary>Toggle the user-mute flag on a Now Playing row.</summary>
    public void ToggleNowPlayingMute(NowPlayingEntry entry)
    {
        if (entry.PlotKey == null)
            streamPlayer.UserMuted = !streamPlayer.UserMuted;
        else
            multiStreamPlayer?.SetVoiceMuted(entry.PlotKey, !entry.Muted);
    }

    /// <summary>
    /// Toggle play/pause on a Now Playing row. No-op for live entries — the
    /// UI already gates the button off in that case, but defending here too
    /// keeps the rule in one place if the entry is stale by the time the
    /// click arrives.
    /// </summary>
    public void ToggleNowPlayingPause(NowPlayingEntry entry)
    {
        if (entry.IsLive) return;
        if (entry.PlotKey == null)
        {
            if (streamPlayer.IsPaused) streamPlayer.Resume();
            else streamPlayer.Pause();
        }
        else
        {
            multiStreamPlayer?.SetVoicePaused(entry.PlotKey, !entry.IsPaused);
        }
    }

    /// <summary>
    /// Move the playhead on a Now Playing row. Clamped to [0, duration] by
    /// the caller; we forward whatever they pass. No-op for live or
    /// non-seekable entries.
    /// </summary>
    public void SeekNowPlaying(NowPlayingEntry entry, double seconds)
    {
        if (entry.IsLive || !entry.Seekable) return;
        if (entry.PlotKey == null)
            streamPlayer.SeekToSeconds(seconds);
        else
            multiStreamPlayer?.SeekVoice(entry.PlotKey, seconds);
    }

    /// <summary>
    /// Advance a Now Playing row to its source's next playlist item. For
    /// single-video URLs (1-item playlist) this exhausts the wrapper, and
    /// the natural-end loop logic decides what happens next (restart on
    /// loop-on, stop on loop-off). No-op for live or non-skippable entries.
    /// </summary>
    public void SkipNowPlayingToNext(NowPlayingEntry entry)
    {
        if (entry.IsLive || !entry.CanSkipNext) return;
        if (entry.PlotKey == null)
            streamPlayer.SkipToNext();
        else
            multiStreamPlayer?.SkipVoiceToNext(entry.PlotKey);
    }

    /// <summary>
    /// Add the row's URL to the blocked list and stop its playback. Future
    /// auto-play (indoor / outdoor / loop) will be gated by Permissions.Check.
    /// </summary>
    public void BlacklistNowPlaying(NowPlayingEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Url)) return;
        Permissions.BlockUrl(entry.Url);
        if (entry.PlotKey == null)
        {
            if (streamPlayer.CurrentUrl == entry.Url) streamPlayer.Stop();
        }
        else
        {
            multiStreamPlayer?.RemoveVoice(entry.PlotKey);
        }
    }

    private bool MultiStreamActive => Config.MultiStreamEnabled;

    private MultiStreamPlayer EnsureMultiStreamPlayer()
    {
        if (multiStreamPlayer == null)
        {
            multiStreamPlayer = new MultiStreamPlayer(Binaries);
            multiStreamPlayer.MasterVolume = Config.Volume;
            // Inherit current focus-mute state — RecomputeFocusMute is event-
            // driven, so a freshly-created player constructed while the user is
            // alt-tabbed would otherwise stay unmuted until the next transition.
            multiStreamPlayer.AutoMuted = !soundsPlayWhenInactive && !gameFocused;
            // Same reason for yt-dlp options: SyncYtDlpOptions is push-on-change,
            // so seed the freshly-created player with current config now.
            multiStreamPlayer.PlaylistRandom = Config.PlaylistRandom;
            multiStreamPlayer.YtDlpCookiesBrowser = Config.YtDlpCookiesBrowser;
            // Single-stream's loop is wired via streamPlayer.StreamNaturallyEnded;
            // multi-stream needs its own per-voice path. The mixer auto-removes
            // voices that EOF, so the framework's outdoor proximity loop will
            // re-add them on the next tick — but only if the user wants loop.
            // Without this gate, every multi-stream voice would loop forever
            // regardless of the LoopFinishedVideos toggle.
            multiStreamPlayer.VoiceNaturallyEnded += OnMultiStreamVoiceNaturallyEnded;
        }
        return multiStreamPlayer;
    }

    private void OnMultiStreamVoiceNaturallyEnded(string canonicalKey, string url)
    {
        Log.Info(
            $"[IND-DIAG] OnMultiStreamVoiceNaturallyEnded key={canonicalKey} url={url} " +
            $"mode={CurrentMode} loopOn={Config.LoopFinishedVideos}");
        if (!Config.LoopFinishedVideos)
        {
            // Suppress the next outdoor-proximity tick from re-adding this
            // voice. Without this entry, HandleOutdoorModeMulti would see
            // "candidate in range, no voice" and call AddVoiceAsync again
            // on the very next tick — bypassing the Loop=OFF intent.
            // Cleared when the user walks out of range / changes territory.
            recentlyEndedMultiKeys.Add(canonicalKey);
            return;
        }
        if (Permissions.Check(url) == UrlDecision.Block)
        {
            Log.Info($"Voice finished but URL is blocked, not looping: {url}");
            return;
        }
        Log.Info($"Voice finished, looping: {url} (key={canonicalKey})");
        // Restart at bypass cutoff; HandleOutdoorModeMulti / HandleIndoorMode
        // will re-apply the correct spatial params on the next framework tick.
        // Returns false if a teardown raced us — fine, the voice is gone.
        _ = multiStreamPlayer?.AddVoiceAsync(canonicalKey, url, MultiStreamPlayer.BypassCutoffHz);
    }

    private void TearDownMultiStream()
    {
        multiStreamPlayer?.StopAll();
        activeMultiDoorPos.Clear();
        // Stop / Play / multi-stream-toggle-off all funnel through here. Drop
        // Loop=OFF suppressions: an explicit teardown is the user signalling
        // "start fresh", so the next outdoor tick should attempt these again.
        recentlyEndedMultiKeys.Clear();
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
        // Honor a blacklist that may have been added between this stream
        // starting and ending — e.g. user hit Blacklist on the Now Playing
        // header. Without this, the loop would re-play a URL the user just
        // explicitly told us to stop.
        if (Permissions.Check(url) == UrlDecision.Block)
        {
            Log.Info($"Stream finished but URL is blocked, not looping: {url}");
            return;
        }
        Log.Info($"Stream finished, looping: {url}");
        // Route through StartStreamAsync (rather than streamPlayer.PlayAsync
        // directly) so pendingStartUrl is set during the 1–3s decoder cold-
        // start. Without that, ApplyAudioPolicy reads streamWillPlay=false in
        // the gap between IsPlaying flipping off (post-EOF) and back on
        // (post-restart), and the game's BGM briefly resumes between loops.
        // CurrentMode is preserved so a Manual loop stays Manual, etc.
        _ = StartStreamAsync(url, CurrentMode, url, announce: false);
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
        string canonicalKey, string displayName, string streamUrl, string description, bool listed,
        string? passphrase = null)
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

        // Plugin-side syntax check before any expensive crypto / network call.
        // For unencrypted publishes the registry repeats this; for encrypted
        // ones the registry can't see the URL, so this is the only validation
        // it ever gets.
        var urlSyntaxErr = StreamUrlValidator.Validate(streamUrl);
        if (urlSyntaxErr != null) throw new InvalidOperationException(urlSyntaxErr);

        var publish = BuildPublishFields(streamUrl, passphrase);
        await registryClient.PublishAsync(
            canonicalKey, publish.streamUrl, displayName, dj, door, listed, description,
            passwordSalt: publish.saltB64,
            encUrl: publish.encUrlB64,
            encNonce: publish.encNonceB64,
            schemaVersion: publish.schemaVersion);

        if (existing == null)
        {
            existing = new ClubEntry();
            Config.PublishedHouses[canonicalKey] = existing;
        }
        existing.DisplayName = displayName;
        existing.StreamUrl = streamUrl;
        existing.Description = description;
        existing.Listed = listed;
        existing.Passphrase = passphrase ?? "";

        // The DJ owns the passphrase by definition — pre-fill both caches so
        // their own indoor / outdoor playback doesn't prompt them for their
        // own stream. Cleared if they re-publish without a passphrase.
        if (!string.IsNullOrWhiteSpace(passphrase))
        {
            Config.KnownClubPassphrases[canonicalKey] = passphrase;
            resolvedPasswordedUrls[canonicalKey] = streamUrl;
        }
        else
        {
            Config.KnownClubPassphrases.Remove(canonicalKey);
            resolvedPasswordedUrls.Remove(canonicalKey);
        }
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
    /// Update name, stream URL, description, and listed flag for an already-
    /// published house. Re-publishes the record (preserving the existing door
    /// coords) signed by the DJ key — the registry's djId match is what gates
    /// this; nobody else can edit your club. Unlike <see cref="PublishHouseAsync"/>,
    /// this does NOT require the DJ to be standing inside the plot, because
    /// door coords are pulled from the existing entry rather than re-derived
    /// from current location.
    /// </summary>
    public async Task RenamePublishedHouseAsync(
        string canonicalKey, string newDisplayName, string newStreamUrl, string newDescription, bool newListed,
        string? newPassphrase = null)
    {
        if (registryClient == null)
            throw new InvalidOperationException("Registry URL not set");
        if (djIdentity == null)
            throw new InvalidOperationException("No DJ identity — cannot edit");
        if (!Config.PublishedHouses.TryGetValue(canonicalKey, out var entry))
            throw new InvalidOperationException("House not in published list");
        if (string.IsNullOrWhiteSpace(newDisplayName))
            throw new InvalidOperationException("Name cannot be empty");
        if (newDisplayName.Length > 80)
            throw new InvalidOperationException("Name too long (max 80 chars)");
        if (string.IsNullOrWhiteSpace(newStreamUrl))
            throw new InvalidOperationException("Stream URL cannot be empty");
        if (newDescription.Length > 500)
            throw new InvalidOperationException("Description too long (max 500 chars)");

        // Plugin-side syntax check before any expensive crypto / network call.
        // Mirrors PublishHouseAsync — see comment there for rationale.
        var urlSyntaxErr = StreamUrlValidator.Validate(newStreamUrl);
        if (urlSyntaxErr != null) throw new InvalidOperationException(urlSyntaxErr);

        var publish = BuildPublishFields(newStreamUrl, newPassphrase);
        var door = HasDoor(entry) ? ToDoorPayload(entry) : null;
        await registryClient.PublishAsync(
            canonicalKey, publish.streamUrl, newDisplayName, djIdentity, door, newListed, newDescription,
            passwordSalt: publish.saltB64,
            encUrl: publish.encUrlB64,
            encNonce: publish.encNonceB64,
            schemaVersion: publish.schemaVersion);

        entry.DisplayName = newDisplayName;
        entry.StreamUrl = newStreamUrl;
        entry.Description = newDescription;
        entry.Listed = newListed;
        entry.Passphrase = newPassphrase ?? "";

        // Mirror PublishHouseAsync: keep the DJ's own auth caches in sync with
        // their published gate so their outdoor / indoor mode never prompts
        // them for their own stream.
        if (!string.IsNullOrWhiteSpace(newPassphrase))
        {
            Config.KnownClubPassphrases[canonicalKey] = newPassphrase;
            resolvedPasswordedUrls[canonicalKey] = newStreamUrl;
        }
        else
        {
            Config.KnownClubPassphrases.Remove(canonicalKey);
            resolvedPasswordedUrls.Remove(canonicalKey);
        }
        Config.Save();

        // Drop the cached ward listing so the DJ's own client refetches the
        // updated displayName for outdoor proximity (rather than serving the
        // stale name from the previous fetch for up to 60s).
        InvalidateWardCacheForDoor(door);

        // If the DJ is themselves listening in Indoor mode for this plot and
        // we just changed the URL, the active stream is stale — switch.
        // Mirrors the equivalent block in PublishHouseAsync.
        if (CurrentMode == PlaybackMode.Indoor
            && CurrentPlotKey.HasValue
            && CurrentPlotKey.Value.Canonical == canonicalKey
            && streamPlayer.CurrentUrl != newStreamUrl)
        {
            EnterIndoor(newStreamUrl, new ClubContext(newDisplayName, newDescription));
        }
    }

    /// <summary>
    /// Build the password-related fields that go on a PublishAsync call.
    /// Empty/null passphrase yields an unprotected publish (all crypto fields
    /// null, plaintext URL on the wire). Non-empty passphrase rotates fresh
    /// salt + nonce and AES-256-GCM-encrypts the URL — the registry only
    /// ever sees the ciphertext.
    ///
    /// Reused across PublishHouseAsync, RenamePublishedHouseAsync, and the
    /// auto-republish that fires after a door calibrate. Keeping the
    /// derivation in one place is what prevents the calibrate path from
    /// silently stripping the password gate (the bug it had before this
    /// helper existed).
    /// </summary>
    private static (
        string streamUrl,
        string? saltB64,
        string? encUrlB64,
        string? encNonceB64,
        int? schemaVersion)
        BuildPublishFields(string streamUrl, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            return (streamUrl, null, null, null, null);

        var salt = Passphrase.GenerateSalt();
        var (ciphertext, nonce) = Passphrase.EncryptUrl(passphrase, salt, streamUrl);
        // streamUrl on the wire is empty for v2 — the registry rejects
        // publishes carrying plaintext alongside ciphertext, and we don't
        // want the URL on the wire even in transit.
        return (
            "",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(nonce),
            2);
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
            Notify("ClubFFXIV: calibrate failed", "Must be standing in an outdoor ward.", NotificationType.Error);
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
                // Carry the existing passphrase through so a calibrate
                // doesn't silently strip the password gate. Re-running the
                // v2 encryption rotates salt + nonce, which is fine — old
                // listeners just decrypt the new ciphertext with the same
                // passphrase on their next refetch.
                var passphrase = pub.Passphrase;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var publish = BuildPublishFields(url, passphrase);
                        await registryClient.PublishAsync(
                            canonicalKey, publish.streamUrl, dn, djIdentity, doorPayload, listed, desc,
                            passwordSalt: publish.saltB64,
                            encUrl: publish.encUrlB64,
                            encNonce: publish.encNonceB64,
                            schemaVersion: publish.schemaVersion);
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
            Notify("ClubFFXIV: calibrate failed", $"No house with key {canonicalKey}", NotificationType.Error);
            return false;
        }

        Config.Save();
        Notify(
            "ClubFFXIV: door calibrated",
            $"Position: ({pos.Value.X:F1}, {pos.Value.Y:F1}, {pos.Value.Z:F1})",
            NotificationType.Success);
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
        // If GameConfig wasn't readable at construction (early plugin load,
        // pre-login), retry per-tick until it is. Stops once seeded — typical
        // cost is one TryGet pair per tick for the first few seconds, then
        // zero. Without this we'd be stuck on the persisted Config value
        // (correct across sessions, but stale if the user changed the FFXIV
        // setting outside the game).
        if (!soundsPlayWhenInactiveSeeded)
            TryRefreshSoundsPlayWhenInactiveFromGameConfig();

        // Focus-edge detection runs every frame (not gated by HousingCheckInterval)
        // so alt-tabbing mutes within ~16ms instead of up to 500ms. The check is
        // a single GetForegroundWindow syscall + pointer compare; recompute only
        // fires on an actual transition.
        var nowFocused = WindowFocus.IsGameFocused();
        if (nowFocused != gameFocused)
        {
            gameFocused = nowFocused;
            RecomputeFocusMute();
        }

        // Frame-rate spatial refresh runs OUTSIDE the housing throttle. The
        // proximity tick at 500ms drives voice add/remove and caches each
        // active door's position; this path uses that cache to update pan +
        // cutoff every frame so camera rotation tracks instantly. Cheap:
        // 1 ObjectTable read + 1 orientation read + (1..N) tiny vec math
        // calls + (1..N) field assignments. Sub-millisecond at any voice
        // count we'll ever have.
        RefreshSpatialPose();

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
    /// Force the next framework tick to refresh housing state immediately rather
    /// than waiting up to 500ms. Called on territory change so BGM mute / auto-play
    /// kicks in within ~1 frame of crossing the instance boundary instead of
    /// half a second later.
    /// </summary>
    private void OnTerritoryChanged(uint territoryType)
    {
        lastHousingCheck = DateTime.MinValue;
        // Reset the "user dismissed this" set so re-entering a ward / plot
        // gets a fresh chance to prompt for any password-protected clubs.
        skippedPasswordedPlots.Clear();
        // Same idea for Loop=OFF suppressions — the candidate set is about to
        // change wholesale, so stale entries from the previous ward shouldn't
        // suppress fresh play attempts in the new one.
        recentlyEndedMultiKeys.Clear();
    }

    /// <summary>
    /// Re-reads FFXIV's "Play sounds when window is not active" toggle when the
    /// user changes it in System Configuration. Fed by IGameConfig.SystemChanged
    /// rather than polled per-tick.
    /// </summary>
    private void OnSystemConfigChanged(object? sender, ConfigChangeEvent e)
    {
        // IsSndBgm: re-snapshot the user's BGM preference if they toggled it
        // while we're muting, so Unmute restores to their current intent.
        if (e.Option is SystemConfigOption.IsSndBgm)
        {
            bgmMuter.OnExternalChange();
            return;
        }
        if (e.Option is not SystemConfigOption.IsSoundAlways && e.Option is not SystemConfigOption.IsSoundBgmAlways)
            return;
        TryRefreshSoundsPlayWhenInactiveFromGameConfig();
    }

    /// <summary>
    /// Reads the "Play sounds when window is not active" policy from FFXIV's
    /// system config — the AND of IsSoundAlways and IsSoundBgmAlways. On a real
    /// change, updates the runtime cache, persists to Config (so the next plugin
    /// launch starts with the right value before GameConfig is readable), and
    /// recomputes the focus-mute policy. Returns false if either option wasn't
    /// readable; OnFrameworkUpdate keeps retrying until it succeeds.
    /// </summary>
    private bool TryRefreshSoundsPlayWhenInactiveFromGameConfig()
    {
        if (!GameConfig.TryGet(SystemConfigOption.IsSoundAlways, out uint soundAlways)) return false;
        if (!GameConfig.TryGet(SystemConfigOption.IsSoundBgmAlways, out uint soundBgmAlways)) return false;
        soundsPlayWhenInactiveSeeded = true;
        var nv = soundAlways != 0 && soundBgmAlways != 0;
        if (nv != soundsPlayWhenInactive)
        {
            soundsPlayWhenInactive = nv;
            RecomputeFocusMute();
        }
        if (Config.SoundsPlayWhenInactive != nv)
        {
            Config.SoundsPlayWhenInactive = nv;
            Config.Save();
        }
        return true;
    }

    /// <summary>
    /// Apply the cached focus-mute state to both stream players. Called only on
    /// transitions (focus change, FFXIV setting toggle, multi-stream player
    /// creation), so we don't write AutoMuted on quiet ticks. AutoMuted setters
    /// short-circuit on equal values, but skipping the call is cheaper still.
    /// </summary>
    private void RecomputeFocusMute()
    {
        var shouldMute = !soundsPlayWhenInactive && !gameFocused;
        streamPlayer.AutoMuted = shouldMute;
        if (multiStreamPlayer != null) multiStreamPlayer.AutoMuted = shouldMute;
    }

    /// <summary>
    /// Push yt-dlp options (playlist shuffle, cookies-from-browser) from config
    /// into both stream players. Call after the user toggles either in the UI;
    /// the values are read at the next CreateAsync (next stream start / loop),
    /// so this doesn't interrupt anything currently playing. Push-on-change
    /// rather than pull-on-tick to keep ApplyAudioPolicy quiet.
    /// </summary>
    public void SyncYtDlpOptions()
    {
        streamPlayer.PlaylistRandom = Config.PlaylistRandom;
        streamPlayer.YtDlpCookiesBrowser = Config.YtDlpCookiesBrowser;
        if (multiStreamPlayer != null)
        {
            multiStreamPlayer.PlaylistRandom = Config.PlaylistRandom;
            multiStreamPlayer.YtDlpCookiesBrowser = Config.YtDlpCookiesBrowser;
        }
    }

    /// <summary>
    /// After audio state is settled for the tick, apply cross-cutting policies:
    /// game BGM muting (when our stream plays). Focus muting lives in
    /// <see cref="RecomputeFocusMute"/> (fired by event/edge); yt-dlp option
    /// sync lives in <see cref="SyncYtDlpOptions"/> (pushed from the UI).
    /// </summary>
    private void ApplyAudioPolicy()
    {
        // Game BGM mute only when stream is the primary audio source (indoor / manual).
        // Outdoor proximity is meant to layer *over* the world's own BGM, not replace it.
        // Mute while a load is pending too — otherwise the game's BGM blares for the
        // 1–3s the stream takes to connect after entering a house.
        // IsActive (not IsPlaying) — a paused manual/indoor stream still "owns"
        // the audio role. Without this, hitting Pause unmutes the game's BGM
        // and you hear it blare over your paused row.
        var streamWillPlay = streamPlayer.IsActive || pendingStartUrl != null;
        if (!streamWillPlay && MultiStreamActive && multiStreamPlayer?.HasAnyActivity == true)
            streamWillPlay = true;
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
        // Manual playback is sticky — never override with auto-play. IsActive
        // (Playing OR Paused) so a paused manual stream doesn't fall through
        // to outdoor proximity and start adding voices alongside it.
        if (CurrentMode == PlaybackMode.Manual && (streamPlayer.IsActive || pendingStartUrl != null))
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

        // Both CurrentPlotKey and CurrentWard are null — but this happens in
        // two very different scenarios:
        //   1. User genuinely left housing (teleported, logged out). Tear down.
        //   2. Threshold-crossing transient: IndoorTerritory just flipped
        //      non-null so IsInOutdoorWard returns false → CurrentWard=null;
        //      ward/plot haven't populated yet so ResolveCurrent returns null
        //      → CurrentPlotKey=null. Tearing down here would silently destroy
        //      the outdoor voice the user is about to seamlessly continue from
        //      inside. Detect the transient by checking HousingManager directly:
        //      if either Indoor- or OutdoorTerritory is non-null, the user is
        //      still in a housing district and the null PlotKey/Ward is
        //      transient — hold voices and wait for the next tick.
        if (HousingDetector.IsInHousingDistrict()) return;

        if (CurrentMode is PlaybackMode.Indoor or PlaybackMode.Outdoor)
        {
            Log.Info($"[IND-DIAG] DriveAudio TEAR-DOWN-LEFT-HOUSING prevMode={CurrentMode}");
            streamPlayer.Stop();
            TearDownMultiStream();
            CurrentMode = PlaybackMode.Off;
            CurrentProximity = null;
        }
    }

    private void HandleIndoorMode(PlotKey key)
    {
        // Indoor mode is solo: prune every tick, BEFORE the alreadySettled
        // check. Otherwise a multi-stream mixer that already has the current
        // plot's voice (e.g., the DJ auto-populated path) would early-return
        // here while outdoor proximity voices for OTHER nearby clubs continue
        // mixing in. RemoveVoice is idempotent (no-op if the key isn't
        // active), so calling every tick is cheap.
        if (MultiStreamActive && multiStreamPlayer != null)
        {
            var activeKeysSnapshot = multiStreamPlayer.ActiveKeys();
            if (CurrentMode != PlaybackMode.Indoor)
            {
                Log.Info(
                    $"[IND-DIAG] HandleIndoorMode FIRST-TICK key={key.Canonical} prevMode={CurrentMode} " +
                    $"activeKeys=[{string.Join(",", activeKeysSnapshot)}] " +
                    $"hasMatching={activeKeysSnapshot.Contains(key.Canonical)}");
            }
            foreach (var activeKey in activeKeysSnapshot)
            {
                if (activeKey != key.Canonical)
                    multiStreamPlayer.RemoveVoice(activeKey);
            }
        }
        else if (CurrentMode == PlaybackMode.Outdoor)
        {
            // Single-stream Outdoor→Indoor handoff: release the WaveOutEvent
            // before EnterIndoor builds a new chain. Stop() is null-safe and
            // idempotent, so we don't gate on IsPlaying — a paused or
            // naturally-EOF'd output is still holding the device handle and
            // needs disposing or a stale handle survives into the next stream.
            streamPlayer.Stop();
        }

        bool alreadySettled;
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
            // IsActive: a paused Indoor stream is still settled — we don't
            // want to re-enter the saved-house / registry lookup just because
            // the user hit Pause on the Now Playing row.
            alreadySettled = CurrentMode == PlaybackMode.Indoor && streamPlayer.IsActive;
        }

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

            // Password-protected: try the cached passphrase first; if that
            // doesn't unlock the URL, prompt the user. We don't keep retrying
            // the prompt across framework ticks — passphrasePromptWindow's
            // queue dedupes by plotKey.
            if (record.PasswordRequired)
            {
                if (await TryUnlockWithCachedPassphrase(key, record) is { } unlocked)
                    record = unlocked;
                else
                {
                    PromptForPassphrase(key, record);
                    return;
                }
            }

            if (string.IsNullOrEmpty(record.StreamUrl)) return;
            EnterIndoor(record.StreamUrl, new ClubContext(record.DisplayName, record.Description));
        }
        catch (Exception ex)
        {
            Log.Warning($"Registry lookup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// If we have a cached passphrase for this plot, resolve the plaintext
    /// stream URL via <see cref="ResolvePasswordedUrlAsync"/> (v2 decrypt or
    /// legacy hash round-trip, depending on the record format). Returns the
    /// input record with <c>StreamUrl</c> filled in on success, null if
    /// there's no cache OR the cached passphrase was rejected.
    /// </summary>
    private async Task<ClubRecord?> TryUnlockWithCachedPassphrase(PlotKey key, ClubRecord record)
    {
        if (string.IsNullOrEmpty(record.PasswordSalt)) return null;
        if (!Config.KnownClubPassphrases.TryGetValue(key.Canonical, out var cached)
            || string.IsNullOrEmpty(cached))
            return null;

        var url = await ResolvePasswordedUrlAsync(key.Canonical, record, cached);
        if (string.IsNullOrEmpty(url))
        {
            // Cached passphrase no longer valid (DJ rotated it). Drop the
            // cache entry so the prompt flow runs cleanly on the next visit.
            Config.KnownClubPassphrases.Remove(key.Canonical);
            Config.Save();
            return null;
        }
        // Opportunistic warm: outdoor proximity reads from this dict.
        resolvedPasswordedUrls[key.Canonical] = url;
        record.StreamUrl = url;
        return record;
    }

    /// <summary>
    /// Resolve the plaintext stream URL for a password-protected record.
    /// v2 records (EncUrl + EncNonce + PasswordSalt) decrypt locally — no
    /// second registry call, AEAD failure means wrong passphrase. Legacy
    /// records (PasswordHash gate, no EncUrl) fall back to the original
    /// flow: derive the Argon2id hash and re-GET with it. Returns null on
    /// any failure (wrong passphrase, malformed base64, network error)
    /// so callers can route to a re-prompt without distinguishing causes.
    /// </summary>
    private async Task<string?> ResolvePasswordedUrlAsync(
        string canonicalKey, ClubRecord record, string passphrase)
    {
        if (string.IsNullOrEmpty(record.PasswordSalt)) return null;

        byte[] salt;
        try { salt = Convert.FromBase64String(record.PasswordSalt); }
        catch (FormatException) { return null; }

        if (!string.IsNullOrEmpty(record.EncUrl) && !string.IsNullOrEmpty(record.EncNonce))
        {
            try
            {
                var ciphertext = Convert.FromBase64String(record.EncUrl);
                var nonce = Convert.FromBase64String(record.EncNonce);
                return Passphrase.DecryptUrl(passphrase, salt, ciphertext, nonce);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        if (registryClient == null) return null;
        try
        {
            var hash = Convert.ToBase64String(Passphrase.HashWithSalt(passphrase, salt));
            var unlocked = await registryClient.GetAsync(canonicalKey, hash);
            if (unlocked == null || string.IsNullOrEmpty(unlocked.StreamUrl)) return null;
            return unlocked.StreamUrl;
        }
        catch (Exception ex)
        {
            Log.Warning($"Legacy passphrase unlock failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Background-resolve the URL for a password-protected club using the
    /// listener's cached passphrase. On success the URL is stored in
    /// <see cref="resolvedPasswordedUrls"/> for outdoor proximity to consume.
    /// On 401 (cached passphrase rejected) the cache entry is dropped so the
    /// next indoor visit prompts cleanly. Single-flight per plot key via
    /// <see cref="resolvingPasswordedUrls"/>; framework ticks fire this off
    /// every ward refetch and we don't want N concurrent identical fetches.
    /// </summary>
    private async Task WarmPasswordedUrlAsync(string plotKey)
    {
        if (registryClient == null) return;
        if (!Config.KnownClubPassphrases.TryGetValue(plotKey, out var passphrase)
            || string.IsNullOrEmpty(passphrase)) return;
        lock (resolvingPasswordedUrls)
        {
            if (resolvingPasswordedUrls.Contains(plotKey)) return;
            resolvingPasswordedUrls.Add(plotKey);
        }
        try
        {
            // First fetch returns metadata. For v2 records this fetch ALSO
            // returns the encUrl/encNonce, so the unified resolver can
            // decrypt without a second round-trip; for legacy records the
            // resolver falls back to the hash-gated re-fetch.
            var meta = await registryClient.GetAsync(plotKey);
            if (meta == null || !meta.PasswordRequired || string.IsNullOrEmpty(meta.PasswordSalt))
            {
                // Club is no longer password-protected (or gone) — clear stale cache.
                resolvedPasswordedUrls.Remove(plotKey);
                return;
            }
            var url = await ResolvePasswordedUrlAsync(plotKey, meta, passphrase);
            if (string.IsNullOrEmpty(url))
            {
                // Stored passphrase rejected — drop both the resolved URL and
                // the now-stale passphrase so the next indoor visit prompts.
                resolvedPasswordedUrls.Remove(plotKey);
                Config.KnownClubPassphrases.Remove(plotKey);
                Config.Save();
                return;
            }
            resolvedPasswordedUrls[plotKey] = url;
        }
        catch (Exception ex)
        {
            Log.Warning($"Warm passworded URL failed for {plotKey}: {ex.Message}");
        }
        finally
        {
            lock (resolvingPasswordedUrls) resolvingPasswordedUrls.Remove(plotKey);
        }
    }

    private void PromptForPassphrase(PlotKey key, ClubRecord record)
    {
        // User said Skip earlier this territory — don't loop the prompt.
        if (skippedPasswordedPlots.Contains(key.Canonical)) return;
        if (passphrasePromptWindow.HasPromptFor(key.Canonical)) return;
        var ctx = new ClubContext(record.DisplayName, record.Description);
        // Capture the full record so HandlePassphraseSubmitted can decrypt
        // the v2 ciphertext we already have rather than re-fetching. For
        // legacy records the captured record only contributes salt /
        // display metadata; the resolver still does its second GET.
        passphrasePromptWindow.Prompt(
            key.Canonical,
            ctx,
            onSubmit: entered => _ = HandlePassphraseSubmitted(key, record, entered),
            onCancel: () => skippedPasswordedPlots.Add(key.Canonical));
        passphrasePromptWindow.EnsureOpenIfPending();
    }

    private async Task HandlePassphraseSubmitted(PlotKey key, ClubRecord record, string entered)
    {
        if (registryClient == null) return;
        try
        {
            var url = await ResolvePasswordedUrlAsync(key.Canonical, record, entered);
            if (string.IsNullOrEmpty(url))
            {
                passphrasePromptWindow.ReportFailure("Wrong passphrase. Try again or Skip.");
                return;
            }
            // Cache for future visits and play.
            Config.KnownClubPassphrases[key.Canonical] = entered;
            resolvedPasswordedUrls[key.Canonical] = url;
            Config.Save();
            passphrasePromptWindow.Resolve();
            if (Nullable.Equals(CurrentPlotKey, key))
                EnterIndoor(url, new ClubContext(record.DisplayName, record.Description));
        }
        catch (Exception ex)
        {
            Log.Warning($"Passphrase verify failed: {ex.Message}");
            passphrasePromptWindow.ReportFailure("Verification failed — check the registry connection.");
        }
    }


    /// <summary>
    /// Enter indoor playback for the given URL. If we were already playing this exact
    /// URL outdoors (player walked from the door into the house), drop the spatial
    /// chain in place — no restart, no rebuffering.
    /// </summary>
    private void EnterIndoor(string url, ClubContext context)
    {
        if (MultiStreamActive)
        {
            EnterIndoorMulti(url, context);
            return;
        }

        // Seamless: same URL was already streaming outdoors. IsActive (not
        // IsPlaying) so a paused stream of the same URL also reuses in place
        // — no reconnect, no rebuffer, no lost pause position.
        if (streamPlayer.IsActive && streamPlayer.CurrentUrl == url)
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
        // Unconditional Stop() releases the WaveOutEvent even if the
        // streamPlayer is in a half-dead state (paused, naturally-EOF'd, or
        // errored mid-stream) — gating on IsPlaying would leave a stale
        // device handle alive alongside the multi-stream output.
        streamPlayer.Stop();

        var canonical = CurrentPlotKey?.Canonical;
        if (canonical == null)
        {
            Log.Info($"[IND-DIAG] EnterIndoorMulti BAIL-no-plot url={url}");
            return;
        }

        var player = EnsureMultiStreamPlayer();
        var keysBeforePrune = player.ActiveKeys();
        Log.Info(
            $"[IND-DIAG] EnterIndoorMulti ENTER url={url} canonical={canonical} " +
            $"activeBeforePrune=[{string.Join(",", keysBeforePrune)}]");

        // Solo: drop every voice that isn't this plot's.
        foreach (var key in player.ActiveKeys())
        {
            if (key != canonical) player.RemoveVoice(key);
        }

        if (player.HasVoice(canonical))
        {
            // Seamless reuse — the outdoor voice stays alive, just unmuffled.
            Log.Info($"[IND-DIAG] EnterIndoorMulti SEAMLESS-REUSE canonical={canonical}");
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
            Log.Info($"[IND-DIAG] EnterIndoorMulti WAITING-FOR-PENDING canonical={canonical}");
            CurrentMode = PlaybackMode.Indoor;
            CurrentProximity = null;
            return;
        }

        if (!TryAutoPlayPermission(url, context))
        {
            Log.Info($"[IND-DIAG] EnterIndoorMulti BAIL-no-permission url={url}");
            return;
        }
        if (BinariesMissingForUrl(url))
        {
            Log.Info($"[IND-DIAG] EnterIndoorMulti BAIL-binaries-missing url={url}");
            return;
        }
        if (IsStreamInCooldown(url))
        {
            Log.Info($"[IND-DIAG] EnterIndoorMulti BAIL-cooldown url={url}");
            return;
        }

        Log.Info($"[IND-DIAG] EnterIndoorMulti FRESH-ADD canonical={canonical} url={url}");
        // Optimistic mode flip — same rationale as the single-stream path.
        CurrentMode = PlaybackMode.Indoor;
        CurrentProximity = null;
        _ = player.AddVoiceAsync(canonical, url, MultiStreamPlayer.BypassCutoffHz);
    }

    /// <summary>
    /// Per-frame refresh of the directional cue (pan + rear-muffled cutoff)
    /// for already-active outdoor voices. Uses door positions cached by the
    /// proximity tick so we skip the candidate enumeration / range filtering
    /// and just rerun the cheap math. Bypassed when directional audio is off,
    /// when the player isn't outdoors, or when no voices are active — the
    /// 500ms proximity tick continues to handle volume falloff at its own
    /// cadence (which has always felt fluid because walking changes distance
    /// slowly).
    /// </summary>
    private void RefreshSpatialPose()
    {
        if (CurrentMode != PlaybackMode.Outdoor) return;

        // Threshold-crossing guard: when the game has just flipped IndoorTerritory
        // non-null, the player is already in the indoor instance's coordinate
        // system but CurrentMode is still Outdoor (DriveAudio is in the transient
        // window before HandleIndoorMode flips it). activeMultiDoorPos still
        // holds the outdoor door's world position — Vector3.Distance between
        // indoor-player and outdoor-door yields a huge number, nearness clamps
        // to 0, and the voice's volume briefly snaps to silence. Skip the
        // refresh when we're not actually in an outdoor ward; the voice keeps
        // its last outdoor pose (which was at/near the door, full volume +
        // open cutoff) until EnterIndoorMulti takes over.
        if (!HousingDetector.IsInOutdoorWard()) return;

        var hasMulti = MultiStreamActive && multiStreamPlayer != null && activeMultiDoorPos.Count > 0;
        var hasSingle = !MultiStreamActive && activeSingleDoorPos.HasValue;
        if (!hasMulti && !hasSingle) return;

        var posOpt = HousingDetector.PlayerPosition();
        if (posOpt == null) return;
        var pos = posOpt.Value;

        // Orientation is only needed for the directional cue (pan + rear-muffle).
        // When directional audio is off, leaving it null means ComputeDirection
        // returns (0, 0) — pan stays centered, rear-muffle is bypassed — but
        // distance-driven volume + cutoff still update at frame rate.
        var orientation = Config.SpatialDirectionalAudio
            ? ListenerOrientationProvider.Get(Config.SpatialInvertPan)
            : null;

        var panStrength = Config.SpatialPanStrength;
        // Live-update the UI's proximity readout for the voice the 500ms tick
        // already chose to display. Other fields (Audible/Streaming/Candidate)
        // only flip at the proximity tick — those still come from there.
        var proxKey = CurrentProximity?.Candidate.CanonicalKey;

        if (hasMulti)
        {
            foreach (var (key, doorPos) in activeMultiDoorPos)
            {
                var distance = Vector3.Distance(pos, doorPos);
                var nearness = WardProximity.Normalize(
                    distance, Config.SpatialFalloffDistance, Config.SpatialFullVolumeDistance);
                var (balance, fade) = WardProximity.ComputeDirection(pos, doorPos, orientation);
                var cutoff = WardProximity.NearnessToCutoff(
                    nearness, Config.SpatialMinCutoffHz, Config.SpatialMaxCutoffHz);
                cutoff = WardProximity.ApplyRearMuffle(cutoff, fade, Config.SpatialRearMuffleStrength);
                multiStreamPlayer!.SetSpatial(key, nearness, cutoff, balance * panStrength);

                if (key == proxKey && CurrentProximity is { } cp)
                {
                    CurrentProximity = cp with
                    {
                        Distance = distance,
                        NormalizedNearness = nearness,
                        Balance = balance,
                        Fade = fade,
                    };
                }
            }
        }
        else
        {
            var doorPos = activeSingleDoorPos!.Value;
            var distance = Vector3.Distance(pos, doorPos);
            var nearness = WardProximity.Normalize(
                distance, Config.SpatialFalloffDistance, Config.SpatialFullVolumeDistance);
            var (balance, fade) = WardProximity.ComputeDirection(pos, doorPos, orientation);
            var cutoff = WardProximity.NearnessToCutoff(
                nearness, Config.SpatialMinCutoffHz, Config.SpatialMaxCutoffHz);
            cutoff = WardProximity.ApplyRearMuffle(cutoff, fade, Config.SpatialRearMuffleStrength);
            streamPlayer.SetSpatial(nearness, cutoff, balance * panStrength);

            if (CurrentProximity is { } cp)
            {
                CurrentProximity = cp with
                {
                    Distance = distance,
                    NormalizedNearness = nearness,
                    Balance = balance,
                    Fade = fade,
                };
            }
        }
    }

    private void HandleOutdoorMode(WardLocation ward)
    {
        var pos = HousingDetector.PlayerPosition();
        if (pos == null) return;

        var orientation = Config.SpatialDirectionalAudio
            ? ListenerOrientationProvider.Get(Config.SpatialInvertPan)
            : null;

        if (MultiStreamActive)
        {
            HandleOutdoorModeMulti(ward, pos.Value, orientation);
            return;
        }

        var candidates = EnumerateCandidates(ward);
        var result = WardProximity.FindClosest(
            pos.Value,
            candidates,
            Config.SpatialStreamDistance,
            Config.SpatialFalloffDistance,
            Config.SpatialFullVolumeDistance,
            orientation);

        // Always store the proximity result (even if out of range) so the UI can
        // show how far the closest club is — useful for calibration & debugging.
        CurrentProximity = result;

        // Streaming is the broader range — keep the stream alive even outside
        // the audible band so the buffer is primed when the player crosses it.
        if (result == null || !result.Value.Streaming)
        {
            // Tear down whenever we have an active auto-play session — Indoor
            // too, not just Outdoor. After teleporting from inside a club to a
            // housing ward where no clubs are in range, DriveAudio routes here
            // with CurrentMode still Indoor; gating on Outdoor alone would let
            // the indoor stream keep playing forever with no log trail.
            if (CurrentMode is PlaybackMode.Outdoor or PlaybackMode.Indoor)
            {
                streamPlayer.Stop();
                CurrentMode = PlaybackMode.Off;
            }
            activeSingleDoorPos = null;
            return;
        }

        var r = result.Value;
        var cutoff = WardProximity.NearnessToCutoff(
            r.NormalizedNearness,
            Config.SpatialMinCutoffHz,
            Config.SpatialMaxCutoffHz);
        cutoff = WardProximity.ApplyRearMuffle(cutoff, r.Fade, Config.SpatialRearMuffleStrength);

        streamPlayer.SetSpatial(r.NormalizedNearness, cutoff, r.Balance * Config.SpatialPanStrength);
        activeSingleDoorPos = r.Candidate.DoorPosition;

        // IsActive (not IsPlaying) so a paused stream of the same URL doesn't
        // get clobbered by a fresh PlayAsync — pausing then walking past the
        // same club used to lose your pause position because !IsPlaying tripped
        // the restart branch.
        var needNewStream = streamPlayer.CurrentUrl != r.Candidate.StreamUrl
            || (CurrentMode != PlaybackMode.Outdoor && !streamPlayer.IsActive);

        if (needNewStream && pendingStartUrl != r.Candidate.StreamUrl)
        {
            var ctx = new ClubContext(r.Candidate.DisplayName, r.Candidate.Description);
            if (!TryAutoPlayPermission(r.Candidate.StreamUrl, ctx)) return;
            if (BinariesMissingForUrl(r.Candidate.StreamUrl)) return;
            if (IsStreamInCooldown(r.Candidate.StreamUrl)) return;

            Log.Info(
                $"Outdoor restart trigger: " +
                $"currentUrlMatches={streamPlayer.CurrentUrl == r.Candidate.StreamUrl} " +
                $"mode={CurrentMode} isActive={streamPlayer.IsActive}");
            _ = StartStreamAsync(r.Candidate.StreamUrl, PlaybackMode.Outdoor, r.Candidate.DisplayName);
        }
        else if (CurrentMode != PlaybackMode.Outdoor && streamPlayer.IsActive)
        {
            // IsActive: a paused stream that matches the in-range candidate
            // still represents an outdoor session — flip CurrentMode so the
            // BGM / proximity invariants downstream don't read the stale mode.
            CurrentMode = PlaybackMode.Outdoor;
        }
    }

    /// <summary>
    /// Outdoor proximity loop for multi-stream mode. Diffs the desired voice
    /// set (in-range candidates, capped) against the active mixer voices:
    /// remove voices for plots out of range or evicted by the cap, add voices
    /// for newly-in-range plots, and update spatial params on still-active
    /// voices. yt-dlp candidates count as 2 against MaxConcurrentStreams.
    /// </summary>
    private void HandleOutdoorModeMulti(
        WardLocation ward,
        System.Numerics.Vector3 pos,
        WardProximity.ListenerOrientation? orientation)
    {
        var candidates = EnumerateCandidates(ward);
        var allInRange = WardProximity.FindAllInRange(
            pos,
            candidates,
            Config.SpatialStreamDistance,
            Config.SpatialFalloffDistance,
            Config.SpatialFullVolumeDistance,
            orientation);

        // UI proximity readout shows the closest club, regardless of whether
        // it ended up with a voice (e.g. cap might have evicted it — unusual,
        // but possible if a much closer non-yt-dlp later appears).
        CurrentProximity = allInRange.Count > 0 ? allInRange[0] : (WardProximity.Result?)null;

        var player = multiStreamPlayer;
        if (allInRange.Count == 0)
        {
            // Tear down whenever we have an active auto-play session — Indoor
            // too, not just Outdoor. After teleporting from inside a club to a
            // housing ward where no clubs are in range, DriveAudio routes here
            // with CurrentMode still Indoor; gating on Outdoor alone would let
            // the indoor voice keep mixing forever with no log trail.
            if (CurrentMode is PlaybackMode.Outdoor or PlaybackMode.Indoor)
            {
                if (player != null) TearDownMultiStream();
                CurrentMode = PlaybackMode.Off;
            }
            activeMultiDoorPos.Clear();
            // Out of range of every candidate ⇒ any Loop=OFF suppressions are
            // for clubs we've walked away from. Drop them so re-entering range
            // gets a fresh play attempt.
            recentlyEndedMultiKeys.Clear();
            return;
        }

        // Per-key walk-out cleanup: if a previously-ended key isn't in range
        // anymore, drop its suppression so re-entering range can play fresh.
        // Use uncapped allInRange (not the capped desired set) so a club
        // evicted by MaxConcurrentStreams keeps its suppression — we haven't
        // actually walked away from it.
        if (recentlyEndedMultiKeys.Count > 0)
        {
            var inRangeKeys = new HashSet<string>();
            foreach (var r in allInRange) inRangeKeys.Add(r.Candidate.CanonicalKey);
            recentlyEndedMultiKeys.RemoveWhere(k => !inRangeKeys.Contains(k));
        }

        // Lazy-init: only build the WaveOutEvent on first actually-in-range tick.
        player ??= EnsureMultiStreamPlayer();

        // The single-voice player may still be allocated from a prior Indoor
        // or Manual session — possibly playing, paused, or in the half-dead
        // state after a natural EOF / stream error where IsPlaying is false
        // but the WaveOutEvent still holds the device. Outdoor multi-stream
        // mode owns the audio output, so unconditionally release it. Stop()
        // is null-safe and idempotent.
        streamPlayer.Stop();

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

        // Refresh the per-frame spatial cache from the desired set. Voices
        // that aren't yet alive (still inside AddVoiceAsync) are included so
        // they pick up the right pan the moment the mixer starts pulling
        // samples from them.
        activeMultiDoorPos.Clear();
        foreach (var r in desired)
        {
            activeMultiDoorPos[r.Candidate.CanonicalKey] = r.Candidate.DoorPosition;

            var url = r.Candidate.StreamUrl;
            var key = r.Candidate.CanonicalKey;
            var cutoff = WardProximity.NearnessToCutoff(
                r.NormalizedNearness,
                Config.SpatialMinCutoffHz,
                Config.SpatialMaxCutoffHz);
            cutoff = WardProximity.ApplyRearMuffle(cutoff, r.Fade, Config.SpatialRearMuffleStrength);

            if (player.HasVoice(key))
            {
                player.SetSpatial(key, r.NormalizedNearness, cutoff, r.Balance * Config.SpatialPanStrength);
                continue;
            }
            if (player.IsStarting(key)) continue;
            // Voice ended naturally with Loop=OFF and the user hasn't walked
            // out of range yet — respect the toggle and skip the re-add.
            if (recentlyEndedMultiKeys.Contains(key)) continue;

            var ctx = new ClubContext(r.Candidate.DisplayName, r.Candidate.Description);
            if (!TryAutoPlayPermission(url, ctx)) continue;
            if (BinariesMissingForUrl(url)) continue;
            if (IsStreamInCooldown(url)) continue;

            // Newly-added voices come up centered (pan=0); RefreshSpatialPose
            // will fix them on the very next frame after they go live.
            _ = player.AddVoiceAsync(key, url, cutoff);
        }

        if (player.VoiceCount > 0 && CurrentMode != PlaybackMode.Outdoor)
        {
            CurrentMode = PlaybackMode.Outdoor;
        }
    }

    private IEnumerable<WardProximity.Candidate> EnumerateCandidates(WardLocation ward)
    {
        // Local entries first — DJ's own calibrations win over registry.
        // Within locals, Published wins over Saved when both exist for the
        // same plot key: the Published entry carries the Passphrase field
        // (the password gate), but the Saved-mirror sync currently drops it,
        // so picking Saved would leak the URL outdoors for password-protected
        // clubs. Walking Published first + skipping duplicate keys in Saved
        // makes the gate authoritative regardless of which copies exist.
        var seen = new HashSet<string>();
        foreach (var (key, entry) in Config.PublishedHouses)
        {
            if (TryLocalToCandidate(key, entry, ward, out var c))
            {
                seen.Add(key);
                yield return c;
            }
        }
        foreach (var (key, entry) in Config.SavedHouses)
        {
            if (seen.Contains(key)) continue;
            if (TryLocalToCandidate(key, entry, ward, out var c))
            {
                seen.Add(key);
                yield return c;
            }
        }

        // Registry-discovered, deduplicated against local keys we've already
        // yielded. Keys present locally but uncalibrated / URL-empty fall
        // through intentionally — the registry copy can fill in what local
        // couldn't (e.g. user saved the name + ward but never finished
        // calibration; the registry has the door coords).
        if (TryGetCachedWard(ward, out var cached))
        {
            foreach (var entry in cached.Clubs)
            {
                if (seen.Contains(entry.PlotKey)) continue;

                // Password-protected entries arrive with empty StreamUrl from
                // the registry — gated behind per-key auth. Substitute the
                // resolved URL we cached after a successful indoor unlock /
                // ward-warm; if we don't have one yet, skip silently. The
                // ward fetch hook (EnsureWardListingAsync) kicks off a warm
                // for any password-protected entry with a cached passphrase,
                // so the next tick will see the URL appear without ever
                // prompting outdoors.
                var url = entry.StreamUrl;
                if (entry.PasswordRequired)
                {
                    if (!resolvedPasswordedUrls.TryGetValue(entry.PlotKey, out url) || string.IsNullOrEmpty(url))
                        continue;
                }
                if (string.IsNullOrEmpty(url)) continue;

                yield return new WardProximity.Candidate(
                    entry.PlotKey,
                    entry.DisplayName,
                    url,
                    new Vector3(entry.Door.X, entry.Door.Y, entry.Door.Z),
                    entry.Description);
            }
        }
    }

    private bool TryLocalToCandidate(
        string key, ClubEntry entry, WardLocation ward, out WardProximity.Candidate candidate)
    {
        candidate = default;
        if (entry.DoorPosition == null) return false;
        if (entry.DoorTerritoryType != ward.TerritoryType) return false;
        if (entry.DoorWard != ward.Ward) return false;
        if (string.IsNullOrWhiteSpace(entry.StreamUrl)) return false;
        // Password-protected local entries must go through the same outdoor
        // gate as registry-discovered ones: the listener must have authenticated
        // (resolvedPasswordedUrls is the proof). The DJ's own publish path
        // populates this dict on success, so their own outdoor mode keeps
        // working without re-entering their own house.
        if (!string.IsNullOrEmpty(entry.Passphrase)
            && !resolvedPasswordedUrls.ContainsKey(key))
            return false;
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

            // Warm the resolved-URL cache for any password-protected entries
            // we have a passphrase for. This lets outdoor proximity play
            // locked clubs without prompting.
            foreach (var entry in listing.Clubs)
            {
                if (entry.PasswordRequired && Config.KnownClubPassphrases.ContainsKey(entry.PlotKey))
                    _ = WarmPasswordedUrlAsync(entry.PlotKey);
            }
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
            // Bare /pclub opens the music player — the listener-facing
            // surface. Settings live behind the title-bar gear or
            // /pclub config.
            OpenMusicPlayer();
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
                    Notify("ClubFFXIV", "Usage: /pclub play <stream-url>", NotificationType.Info);
                    return;
                }
                try
                {
                    Config.LastStreamUrl = rest;
                    Config.Save();
                    PlayStream(rest);
                    Notify("ClubFFXIV", $"Playing {rest}", NotificationType.Info);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to start stream");
                    Notify("ClubFFXIV: stream failed", ex.Message, NotificationType.Error);
                }
                break;

            case "stop":
                StopStream();
                Notify("ClubFFXIV", "Stopped", NotificationType.Info);
                break;

            case "calibrate":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Notify("ClubFFXIV", "Usage: /pclub calibrate <plotKey>", NotificationType.Info);
                    return;
                }
                CalibrateDoor(rest);
                break;

            case "config":
                OpenConfig();
                break;

            case "mini":
                ToggleMiniPlayer();
                break;

            case "directory":
            case "browse":
                ToggleDirectory();
                break;

            default:
                Notify(
                    "ClubFFXIV",
                    "Usage: /pclub (music player) | mini | play <url> | stop | calibrate <key> | config | directory",
                    NotificationType.Info);
                break;
        }
    }

    private void DrawUI() => WindowSystem.Draw();

    private void OpenConfig() => configWindow.Toggle();
    private void OpenMusicPlayer() => musicPlayerWindow.Toggle();

    /// <summary>
    /// Toggle the settings window. Public so the music player's title-bar
    /// gear button can route here without dragging the window reference
    /// across the UI boundary.
    /// </summary>
    public void ToggleConfig() => configWindow.Toggle();

    /// <summary>Toggle the mini player — bound to <c>/pclub mini</c>.</summary>
    public void ToggleMiniPlayer() => miniPlayerWindow.Toggle();

    /// <summary>
    /// Close the full music player and open the mini. Both windows render
    /// the same Now Playing entries against the same Plugin actions, so
    /// this is just a presentation swap. Bound to the music player's
    /// title-bar "compress" button.
    /// </summary>
    public void SwapToMiniPlayer()
    {
        musicPlayerWindow.IsOpen = false;
        miniPlayerWindow.IsOpen = true;
    }

    /// <summary>
    /// Mirror of <see cref="SwapToMiniPlayer"/>: close the mini and open
    /// the full player. Bound to the mini's title-bar "expand" button.
    /// </summary>
    public void SwapToMusicPlayer()
    {
        miniPlayerWindow.IsOpen = false;
        musicPlayerWindow.IsOpen = true;
    }

    private readonly record struct CachedWardListing(DateTime FetchedAt, WardListing Listing);
}

public enum PlaybackMode
{
    Off,
    Manual,
    Indoor,
    Outdoor,
}
