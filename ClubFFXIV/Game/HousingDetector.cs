using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ClubFFXIV.Game;

/// <summary>
/// Wraps FFXIVClientStructs HousingManager. Resolves both indoor (which house
/// instance you're in) and outdoor (which ward you're roaming) state.
/// </summary>
public sealed class HousingDetector
{
    public unsafe PlotKey? ResolveCurrent()
    {
        var local = Plugin.PlayerState;
        if (local == null) return null;

        var hm = HousingManager.Instance();
        if (hm == null) return null;

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
    /// True when the player is roaming an outdoor housing ward (not inside a house).
    /// </summary>
    public unsafe bool IsInOutdoorWard()
    {
        var hm = HousingManager.Instance();
        if (hm == null) return false;
        return hm->OutdoorTerritory != null && hm->IndoorTerritory == null;
    }

    /// <summary>
    /// Outdoor ward context if the player is roaming a ward, else null.
    /// </summary>
    public unsafe WardLocation? ResolveOutdoor()
    {
        if (!IsInOutdoorWard()) return null;

        var hm = HousingManager.Instance();
        if (hm == null) return null;

        return new WardLocation(
            TerritoryType: Plugin.ClientState.TerritoryType,
            Ward: hm->GetCurrentWard(),
            Division: hm->GetCurrentDivision());
    }

    /// <summary>
    /// Player's current world-space position, or null if no character loaded.
    /// </summary>
    public Vector3? PlayerPosition()
    {
        var pc = Plugin.ClientState.LocalPlayer;
        return pc?.Position;
    }

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

public readonly record struct WardLocation(uint TerritoryType, int Ward, int Division);
