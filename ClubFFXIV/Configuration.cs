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

    public string RegistryUrl { get; set; } = "";
    public bool AutoQueryRegistry { get; set; } = true;
    public string DjPrivateKeyBase64 { get; set; } = "";

    /// <summary>
    /// Phase 4 spatial knobs. Distances are in FFXIV world units (≈ meters).
    /// Cutoffs are in Hz.
    /// </summary>
    public float SpatialFalloffDistance { get; set; } = 40f;     // beyond this: silent
    public float SpatialFullVolumeDistance { get; set; } = 3f;   // closer than this: full volume
    public float SpatialMinCutoffHz { get; set; } = 400f;        // most muffled
    public float SpatialMaxCutoffHz { get; set; } = 8000f;       // clearest at door

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
