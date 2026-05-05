using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace ClubFFXIV;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    public string LastStreamUrl { get; set; } = "";
    public float Volume { get; set; } = 0.7f;

    /// <summary>
    /// Personal auto-play overrides keyed by PlotKey.Canonical.
    /// Always preferred over the registry — your local settings win.
    /// </summary>
    public Dictionary<string, ClubEntry> SavedHouses { get; set; } = new();

    /// <summary>
    /// Houses you've published to the registry. Tracked locally so you can manage
    /// (re-publish, unpublish) without re-discovering. Keyed by PlotKey.Canonical.
    /// </summary>
    public Dictionary<string, ClubEntry> PublishedHouses { get; set; } = new();

    /// <summary>
    /// Base URL of the registry (e.g. "https://clubffxiv-registry.workers.dev").
    /// Empty disables registry features — plugin still works for local SavedHouses.
    /// </summary>
    public string RegistryUrl { get; set; } = "";

    public bool AutoQueryRegistry { get; set; } = true;

    /// <summary>
    /// Ed25519 private key (raw, base64). Generated lazily on first publish.
    /// SENSITIVE: anyone with this can impersonate you in the registry.
    /// Back up your plugin config if you publish clubs you care about.
    /// </summary>
    public string DjPrivateKeyBase64 { get; set; } = "";

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
