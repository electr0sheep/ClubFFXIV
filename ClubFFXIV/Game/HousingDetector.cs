using FFXIVClientStructs.FFXIV.Client.Game.Housing;

namespace ClubFFXIV.Game;

/// <summary>
/// Wraps FFXIVClientStructs HousingManager to detect the current house instance.
/// Phase 2 only resolves indoor (inside-the-house) state; outdoor ward proximity is Phase 4.
/// </summary>
public sealed class HousingDetector
{
    /// <summary>
    /// Returns the player's current house, or null if not currently inside one.
    /// </summary>
    public unsafe PlotKey? ResolveCurrent()
    {
        var local = Plugin.ClientState.LocalPlayer;
        if (local == null) return null;

        var hm = HousingManager.Instance();
        if (hm == null) return null;

        // IndoorTerritory is non-null only when the player is physically inside a house instance.
        if (hm->IndoorTerritory == null) return null;

        var worldId = local.CurrentWorld.RowId;
        var territory = Plugin.ClientState.TerritoryType;
        var ward = hm->GetCurrentWard();
        var plot = hm->GetCurrentPlot();
        var room = hm->GetCurrentRoom();
        var division = hm->GetCurrentDivision();

        return new PlotKey(worldId, territory, ward, plot, room, division);
    }

    /// <summary>
    /// Friendly display name for a saved house. Phase 5 will replace the territory→district
    /// table with a proper Lumina lookup; for now the well-known interior IDs are inlined.
    /// </summary>
    public string GetDisplayName(PlotKey key)
    {
        var district = key.TerritoryType switch
        {
            282 or 283 or 284 => "Mist",
            342 or 343 or 344 => "Lavender Beds",
            345 or 346 or 347 => "Goblet",
            649 or 650 or 651 => "Shirogane",
            980 or 981 or 982 => "Empyreum",
            _ => $"Territory {key.TerritoryType}"
        };

        if (key.Room > 0)
            return $"{district} Ward {key.Ward + 1}, Apt {key.Room}";

        var sub = key.Division == 2 ? " (Subdivision)" : "";
        return $"{district} Ward {key.Ward + 1}, Plot {key.Plot + 1}{sub}";
    }
}
