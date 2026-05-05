using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

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
        var pc = Plugin.ObjectTable.LocalPlayer;
        return pc?.Position;
    }

    /// <summary>
    /// Best-effort check for whether the player owns the house they're currently in.
    ///
    /// Returns Unknown when we can't read the data (e.g. not in a house, or
    /// FFXIVClientStructs API mismatch). Callers should treat Unknown as "allow with
    /// warning" — never block on Unknown, because that would lock out legitimate
    /// users when the API drifts.
    ///
    /// Coverage:
    ///   - Personal houses: compares the player's owned-house ID against the current.
    ///   - FC houses: any FC member counts as owner.
    ///   - Apartments: tenant only.
    /// </summary>
    public unsafe HouseOwnership CheckOwnership()
    {
        var hm = HousingManager.Instance();
        if (hm == null || hm->IndoorTerritory == null) return HouseOwnership.Unknown;
        if (Plugin.ClientState.LocalPlayer == null) return HouseOwnership.Unknown;

        try
        {
            // Personal house owner check: player has an "owned house id"; if it matches
            // the current house id, you're the owner.
            var currentHouseId = (long)hm->IndoorTerritory->HouseId;
            var ownedHouseId = (long)hm->GetOwnedHouseId();
            if (ownedHouseId != 0 && ownedHouseId == currentHouseId)
                return HouseOwnership.Owner;

            // FC house: if the current player's FC owns this estate, treat as owner.
            // FFXIVClientStructs exposes FC info on the local player; the indoor
            // territory has an OwnerId we can compare.
            // (If the API has shifted in your version, the catch below covers it.)
            var indoorOwnerId = hm->IndoorTerritory->OwnerId;
            var localFcId = Plugin.PlayerState.FreeCompanyInfo.Id;
            if (localFcId != 0 && indoorOwnerId == localFcId)
                return HouseOwnership.Owner;

            return HouseOwnership.NotOwner;
        }
        catch
        {
            // Any field-name mismatch in FFXIVClientStructs — be conservative
            // (don't block legitimate users when we can't read the data).
            return HouseOwnership.Unknown;
        }
    }

    public string GetDisplayName(PlotKey key)
    {
        var district = LookupDistrictName(key.TerritoryType);

        if (key.Room > 0)
            return $"{district} Ward {key.Ward + 1}, Apt {key.Room}";

        var sub = key.Division == 2 ? " (Subdivision)" : "";
        return $"{district} Ward {key.Ward + 1}, Plot {key.Plot + 1}{sub}";
    }

    public string LookupDistrictName(uint territoryType)
    {
        if (districtNameCache.TryGetValue(territoryType, out var cached))
            return cached;

        var resolved = ResolveDistrictName(territoryType);
        districtNameCache[territoryType] = resolved;
        return resolved;
    }

    private readonly Dictionary<uint, string> districtNameCache = new();

    private static string ResolveDistrictName(uint territoryType)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet == null) return $"Territory {territoryType}";

            var row = sheet.GetRowOrDefault(territoryType);
            if (row == null) return $"Territory {territoryType}";

            // For housing territories, PlaceName is often the building type
            // ("Private Cottage", "Apartment Suite") while PlaceNameZone is the
            // district ("Mist"). Prefer Zone, fall back to PlaceName, fall back
            // to the bare ID so we never display nothing.
            var zone = row.Value.PlaceNameZone.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(zone)) return zone!;

            var place = row.Value.PlaceName.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(place)) return place!;

            return $"Territory {territoryType}";
        }
        catch
        {
            return $"Territory {territoryType}";
        }
    }
}

public readonly record struct WardLocation(uint TerritoryType, int Ward, int Division);

public enum HouseOwnership
{
    /// <summary>Couldn't determine — not in a house, or API mismatch.</summary>
    Unknown,
    /// <summary>Player owns this house (or is a member of the FC that does).</summary>
    Owner,
    /// <summary>Player is in someone else's house.</summary>
    NotOwner,
}
