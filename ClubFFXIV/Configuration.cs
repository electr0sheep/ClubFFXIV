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
    // Users can point at their own backend by changing this in /club config; clearing
    // it disables registry features (local SavedHouses still work).
    public string RegistryUrl { get; set; } = "https://clubffxiv-registry.clubffxiv-registry.workers.dev";
    public bool AutoQueryRegistry { get; set; } = true;
    public string DjPrivateKeyBase64 { get; set; } = "";

    /// <summary>
    /// Phase 4 spatial knobs. Distances are in FFXIV world units (≈ meters).
    /// Cutoffs are in Hz.
    /// </summary>
    public float SpatialFalloffDistance { get; set; } = 40f;     // beyond this: silent
    public float SpatialFullVolumeDistance { get; set; } = 3f;   // closer than this: full volume
    public float SpatialMinCutoffHz { get; set; } = 400f;        // most muffled (far)
    // Outdoor cap stays well below indoor's bypass (20 kHz), so even right at the
    // door you hear a muffled "wall in the way" sound. Crossing the threshold into
    // the house is what removes the lowpass entirely.
    public float SpatialMaxCutoffHz { get; set; } = 2500f;       // muffled even at door

    /// <summary>Mute the game's own BGM while our stream is playing.</summary>
    public bool MuteGameBgmWhilePlaying { get; set; } = true;

    /// <summary>Mute our stream when the FFXIV window loses focus.</summary>
    public bool MuteStreamWhenUnfocused { get; set; } = true;

    /// <summary>
    /// Bypass the local "are you the owner of this house?" check at publish time.
    /// Off by default — turning it on lets you publish to plots you don't own per
    /// the game's records. Use only if the ownership detection is wrong (FC
    /// edge cases, FFXIVClientStructs API drift, etc.).
    /// </summary>
    public bool AllowPublishWithoutOwnership { get; set; } = false;

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
