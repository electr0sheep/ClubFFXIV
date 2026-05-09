using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace ClubFFXIV;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    public string LastStreamUrl { get; set; } = "";
    public float Volume { get; set; } = 0.7f;

    public Dictionary<string, ClubEntry> SavedHouses { get; set; } = new();
    public Dictionary<string, ClubEntry> PublishedHouses { get; set; } = new();

    /// <summary>
    /// Per-plot passphrase cache for clubs the user has authenticated against.
    /// Keyed by canonical plot key. Populated when the user enters a passphrase
    /// at the prompt, consumed silently on subsequent visits so they don't
    /// re-prompt every time. Plain text by design — same threat model as the
    /// browser cookies we read for yt-dlp; a local config compromise already
    /// owns much more than these.
    /// </summary>
    public Dictionary<string, string> KnownClubPassphrases { get; set; } = new();

    // Defaults to the public ClubFFXIV registry so the plugin works out of the box.
    // Users can point at their own backend by changing this in /pclub config; clearing
    // it disables registry features (local SavedHouses still work).
    public string RegistryUrl { get; set; } = "https://registry.clubffxiv.workers.dev";
    public string DjPrivateKeyBase64 { get; set; } = "";

    /// <summary>
    /// Phase 4 spatial knobs. Distances are in FFXIV world units (≈ meters).
    /// Cutoffs are in Hz. Defaults are exposed as constants so the UI's
    /// "Reset to defaults" button stays in sync with the property initializers.
    /// </summary>
    public const float DefaultSpatialStreamDistance = 60f;
    public const float DefaultSpatialFalloffDistance = 40f;
    public const float DefaultSpatialFullVolumeDistance = 3f;
    public const float DefaultSpatialMinCutoffHz = 400f;
    // Outdoor cap stays well below indoor's bypass (20 kHz), so even right at the
    // door you hear a muffled "wall in the way" sound. Crossing the threshold into
    // the house is what removes the lowpass entirely.
    public const float DefaultSpatialMaxCutoffHz = 2500f;
    public const bool DefaultSpatialDirectionalAudio = true;
    public const float DefaultSpatialRearMuffleStrength = 0.5f;
    public const bool DefaultSpatialInvertPan = false;

    /// <summary>
    /// Beyond this distance the stream isn't connected at all. Inside it (but
    /// outside SpatialFalloffDistance) the stream is alive at volume 0 — pre-buffering
    /// so the audio is ready to fade in the moment the player crosses the audible threshold.
    /// </summary>
    public float SpatialStreamDistance { get; set; } = DefaultSpatialStreamDistance;
    public float SpatialFalloffDistance { get; set; } = DefaultSpatialFalloffDistance;
    public float SpatialFullVolumeDistance { get; set; } = DefaultSpatialFullVolumeDistance;
    public float SpatialMinCutoffHz { get; set; } = DefaultSpatialMinCutoffHz;
    public float SpatialMaxCutoffHz { get; set; } = DefaultSpatialMaxCutoffHz;

    /// <summary>
    /// Stereo-balance the outdoor proximity audio according to where each
    /// door sits relative to the camera (or character, if camera read fails).
    /// Off → all voices play centered, exactly like the pre-directional build.
    /// </summary>
    public bool SpatialDirectionalAudio { get; set; } = DefaultSpatialDirectionalAudio;

    /// <summary>
    /// 0..1: how much extra muffle the lowpass picks up when a door is
    /// directly behind the listener. 0 disables the rear cue (front and back
    /// sound identical, distinguished only by L/R pan). 1 halves the cutoff
    /// at full rearness — strong but still musical. Default 0.5 is a subtle
    /// "duller-when-behind" hint.
    /// </summary>
    public float SpatialRearMuffleStrength { get; set; } = DefaultSpatialRearMuffleStrength;

    /// <summary>
    /// Flip the L/R pan sign. Useful if the FFXIV coordinate-handedness or
    /// camera-vector convention end up backwards in a future game patch (the
    /// math has been double-checked, but a one-toggle escape hatch is cheap
    /// and beats waiting for a plugin update).
    /// </summary>
    public bool SpatialInvertPan { get; set; } = DefaultSpatialInvertPan;

    /// <summary>
    /// Restore all spatial-audio knobs to their factory defaults. Caller is
    /// responsible for invoking <see cref="Save"/> afterwards (matches the
    /// existing pattern where UI handlers explicitly persist).
    /// </summary>
    public void ResetSpatialTuningToDefaults()
    {
        SpatialStreamDistance = DefaultSpatialStreamDistance;
        SpatialFalloffDistance = DefaultSpatialFalloffDistance;
        SpatialFullVolumeDistance = DefaultSpatialFullVolumeDistance;
        SpatialMinCutoffHz = DefaultSpatialMinCutoffHz;
        SpatialMaxCutoffHz = DefaultSpatialMaxCutoffHz;
        SpatialDirectionalAudio = DefaultSpatialDirectionalAudio;
        SpatialRearMuffleStrength = DefaultSpatialRearMuffleStrength;
        SpatialInvertPan = DefaultSpatialInvertPan;
    }

    /// <summary>
    /// Keep the stream playing when entering FC sub-territories (Company Workshop,
    /// basement / private chamber). The game treats these as separate territories
    /// from the house interior, but for music continuity we treat them as still
    /// inside the same plot.
    /// </summary>
    public bool KeepPlayingInLinkedSubterritories { get; set; } = true;

    /// <summary>
    /// Auto-update yt-dlp / ffmpeg every few days in the background.
    /// yt-dlp updates frequently because Twitch and YouTube change their
    /// scraping defenses; without this the plugin will eventually fail on
    /// those URLs until the user manually updates.
    /// </summary>
    public bool AutoUpdateBinaries { get; set; } = true;

    /// <summary>
    /// When a finite source (e.g. a single YouTube video) reaches its end,
    /// automatically restart it from the beginning. Live/indefinite streams
    /// (Twitch, Icecast) never reach a natural EOF, so this flag is a no-op
    /// for them.
    /// </summary>
    public bool LoopFinishedVideos { get; set; } = false;

    /// <summary>
    /// When the URL is a playlist, shuffle it before yt-dlp picks the single
    /// item to play. Combined with <see cref="LoopFinishedVideos"/> this gives
    /// a never-ending random rotation through the playlist. Off = playlist
    /// item 1 plays every time (and every loop iteration replays it).
    /// </summary>
    public bool PlaylistRandom { get; set; } = false;

    /// <summary>
    /// Browser to read cookies from for yt-dlp invocations. Empty = no cookies
    /// (default). Setting "firefox", "chrome", "edge", "brave", "opera", "vivaldi",
    /// "chromium", or "safari" has yt-dlp authenticate as your logged-in browser
    /// session — the standard fix for YouTube's "Sign in to confirm you're not a bot"
    /// screen. You must be logged into the relevant site in that browser.
    /// Pushed to both <see cref="StreamPlayer"/> and <see cref="MultiStreamPlayer"/>
    /// when the user edits the field; changes take effect on the next stream start.
    /// </summary>
    public string YtDlpCookiesBrowser { get; set; } = "";

    /// <summary>Last time we checked for binary updates (UTC).</summary>
    public DateTime BinariesLastChecked { get; set; } = DateTime.MinValue;

    /// <summary>
    /// URL permission lists. Exact-URL matches win over domain matches; allow
    /// matches win over block matches. Anything not on either list prompts the
    /// user when first encountered.
    /// </summary>
    public HashSet<string> AllowedDomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BlockedDomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BlockedUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once the user has been shown the first-run setup wizard.</summary>
    public bool SetupWizardComplete { get; set; } = false;

    /// <summary>
    /// Multi-voice spatial mode. When on, the outdoor proximity loop can keep
    /// multiple clubs streaming simultaneously, mixed by per-voice distance.
    /// Indoor mode still plays one club solo.
    /// </summary>
    public bool MultiStreamEnabled { get; set; } = false;

    /// <summary>
    /// Hard cap on simultaneous outdoor voices. yt-dlp voices count as 2 (each
    /// spawns a yt-dlp + ffmpeg subprocess), so default 2 = "1 yt-dlp OR 2
    /// direct" — a deliberately conservative ceiling.
    /// </summary>
    public int MaxConcurrentStreams { get; set; } = 2;

    /// <summary>
    /// Show artwork thumbnails in the Now Playing header strip. yt-dlp sources
    /// (YouTube/SoundCloud/etc.) provide a thumbnail URL via the --print
    /// template; Icecast and raw-HTTP streams don't, so they render a
    /// placeholder square. Off = text-only rows (the pre-thumbnail layout).
    /// </summary>
    public bool ShowNowPlayingThumbnails { get; set; } = true;

    /// <summary>
    /// Cached copy of FFXIV's "Play sounds when window is not active" policy
    /// (the AND of IsSoundAlways and IsSoundBgmAlways). Updated whenever
    /// IGameConfig.SystemChanged fires for either option, and persisted so
    /// alt-tab muting works at startup before GameConfig is readable
    /// (early plugin load, title screen, etc.). NOT exposed in the UI —
    /// the policy mirrors FFXIV's own setting; this is just a durable
    /// last-known value, not a user-tunable knob.
    /// </summary>
    public bool SoundsPlayWhenInactive { get; set; } = true;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}

[Serializable]
public class ClubEntry
{
    public string DisplayName { get; set; } = "";
    public string StreamUrl { get; set; } = "";

    /// <summary>
    /// DJ-authored description. Shown in the public directory and in URL
    /// permission prompts. Capped at 500 chars on the server; new entries
    /// default to empty.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// World-space coordinates of the front door for proximity audio.
    /// Set by the calibration flow ("stand at the door, hit Calibrate").
    /// Null = no spatial audio for this house — falls back to inside-only auto-play.
    /// </summary>
    public Position3? DoorPosition { get; set; }

    /// <summary>
    /// Outdoor ward identifier the door is in. Used to filter candidate houses
    /// when the player is roaming a ward (avoid scanning every saved house every tick).
    /// </summary>
    public uint? DoorTerritoryType { get; set; }
    public int? DoorWard { get; set; }

    /// <summary>
    /// Whether the club appears in the public directory browse list. Defaults
    /// to true so houses saved/published before this field existed remain
    /// listed after upgrade. Hiding from the directory does NOT hide the
    /// record from per-plot lookup or ward-proximity discovery — that's a
    /// directory-list-only flag; full privacy = don't publish.
    /// </summary>
    public bool Listed { get; set; } = true;

    /// <summary>
    /// Plaintext passphrase for password-protected clubs (PublishedHouses only).
    /// Generated locally via <see cref="ClubFFXIV.Network.Passphrase.Generate"/>;
    /// the plaintext is stored on disk so the DJ can re-show it to share with
    /// friends. Only an Argon2id hash + salt of this passphrase is sent to the
    /// registry — the registry never sees the plaintext. Empty/null = no
    /// password gate. Local SavedHouses don't need this; the field is here on
    /// the shared <see cref="ClubEntry"/> type for convenience but only used
    /// on PublishedHouses entries.
    /// </summary>
    public string Passphrase { get; set; } = "";
}

[Serializable]
public class Position3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Position3() { }
    public Position3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public Position3(System.Numerics.Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }

    public System.Numerics.Vector3 ToVec() => new(X, Y, Z);
}
