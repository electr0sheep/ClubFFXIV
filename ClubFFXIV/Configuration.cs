using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace ClubFFXIV;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public string LastStreamUrl { get; set; } = "";
    public float Volume { get; set; } = 0.7f;

    /// <summary>
    /// Stream URL stored per house. Key is PlotKey.Canonical.
    /// </summary>
    public Dictionary<string, ClubEntry> SavedHouses { get; set; } = new();

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
}
