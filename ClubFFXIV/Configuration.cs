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
    }

    /// <summary>Mute our stream when the FFXIV window loses focus.</summary>
    public bool MuteStreamWhenUnfocused { get; set; } = true;

    /// <summary>
    /// Keep the stream playing when entering FC sub-territories (Company Workshop,
    /// basement / private chamber). The game treats these as separate territories
    /// from the house interior, but for music continuity we treat them as still
    /// inside the same plot.
    /// </summary>
    public bool KeepPlayingInLinkedSubterritories { get; set; } = true;

    /// <summary>
    /// Bypass the local "are you the owner of this house?" check at publish time.
    /// Off by default — turning it on lets you publish to plots you don't own per
    /// the game's records. Use only if the ownership detection is wrong (FC
    /// edge cases, FFXIVClientStructs API drift, etc.).
    /// </summary>
    public bool AllowPublishWithoutOwnership { get; set; } = false;

    /// <summary>
    /// Auto-update yt-dlp / ffmpeg every few days in the background.
    /// yt-dlp updates frequently because Twitch and YouTube change their
    /// scraping defenses; without this the plugin will eventually fail on
    /// those URLs until the user manually updates.
    /// </summary>
    public bool AutoUpdateBinaries { get; set; } = true;

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
