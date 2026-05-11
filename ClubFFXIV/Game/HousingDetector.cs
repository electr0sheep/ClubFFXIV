using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInspect;

namespace ClubFFXIV.Game;

/// <summary>
/// Wraps FFXIVClientStructs HousingManager. Resolves both indoor (which house
/// instance you're in) and outdoor (which ward you're roaming) state.
/// </summary>
public sealed class HousingDetector
{
    // Last plot we resolved while actually inside a house. Used to keep
    // auto-play alive when the player walks into linked sub-territories
    // (FC workshop, basement / private chamber) where HousingManager.IndoorTerritory
    // goes null but the player hasn't really left the house.
    private PlotKey? lastIndoorPlot;

    public unsafe PlotKey? ResolveCurrent(bool keepPlayingInLinkedSubterritories = true)
    {
        var local = Plugin.PlayerState;
        if (local == null) return null;

        var hm = HousingManager.Instance();
        if (hm == null) return null;

        if (hm->IndoorTerritory != null)
        {
            var worldId = local.CurrentWorld.RowId;
            var territory = Plugin.ClientState.TerritoryType;
            var ward = hm->GetCurrentWard();
            var plot = hm->GetCurrentPlot();
            var room = hm->GetCurrentRoom();
            var division = hm->GetCurrentDivision();

            // Threshold-crossing transient: the game flips IndoorTerritory to
            // non-null ~500ms before populating ward/plot, returning -1 placeholders.
            // Returning a PlotKey with -1 would make HandleIndoorMode prune every
            // outdoor voice as non-matching, including the one for the plot the
            // user is actually about to enter — and the next-tick re-add restarts
            // playback from scratch. Hold the previous indoor plot (or null if
            // none) until real values land; the audio driver treats null as "no
            // location change" and leaves voices alone.
            if (ward < 0 || plot < 0) return lastIndoorPlot;

            var resolved = new PlotKey(worldId, territory, ward, plot, room, division);
            lastIndoorPlot = resolved;
            return resolved;
        }

        // FC sub-territories (workshop, basement) — IndoorTerritory is null but
        // the player hasn't really left the house. Continue treating them as
        // inside the last resolved plot so the stream keeps playing.
        if (keepPlayingInLinkedSubterritories
            && IsHouseLinkedSubterritory(Plugin.ClientState.TerritoryType))
            return lastIndoorPlot;

        // Truly left housing.
        lastIndoorPlot = null;
        return null;
    }

    /// <summary>
    /// Sub-territories that hang off an FC house we want to keep playing in.
    /// Currently scoped to FC workshops only — player rooms / private chambers
    /// are deliberately excluded since they're personal spaces, not the venue
    /// the user came in for.
    ///
    /// Detected by territory PlaceName containing "Workshop" rather than a
    /// hardcoded ID list — works across future SE additions and avoids the
    /// risk of stale IDs.
    /// </summary>
    private static bool IsHouseLinkedSubterritory(uint territory)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet == null) return false;
            var row = sheet.GetRowOrDefault(territory);
            if (row == null) return false;
            var name = row.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            return name.Contains("Workshop", System.StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
    /// True when the player is anywhere in a housing district — indoor, outdoor,
    /// or mid-threshold-crossing (when IndoorTerritory has flipped non-null but
    /// the ward/plot fields haven't populated yet, so <see cref="ResolveCurrent"/>
    /// returns null and <see cref="ResolveOutdoor"/> returns null too). DriveAudio
    /// uses this to distinguish the transient (keep voices alive) from a real
    /// "left housing" event (teleport, logout — tear voices down).
    /// </summary>
    public unsafe bool IsInHousingDistrict()
    {
        var hm = HousingManager.Instance();
        if (hm == null) return false;
        return hm->IndoorTerritory != null || hm->OutdoorTerritory != null;
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
        if (Plugin.ObjectTable.LocalPlayer == null) return HouseOwnership.Unknown;

        try
        {
            // Personal house owner check: player has an "owned house id"; if it matches
            // the current house id, you're the owner.
            var currentHouseId = hm->IndoorTerritory->HouseId.Id;
            var ownedHouseId = HousingManager.GetOwnedHouseId(EstateType.PersonalEstate).Id;
            if (ownedHouseId != 0 && ownedHouseId == currentHouseId)
                return HouseOwnership.Owner;

            // FC house: if the current player's FC owns this estate, treat as owner.
            // FFXIVClientStructs exposes FC info on the local player; the indoor
            // territory has an OwnerId we can compare.
            // (If the API has shifted in your version, the catch below covers it.)
            var localFcId = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate).Id;
            if (localFcId != 0 && currentHouseId == localFcId)
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

    /// <summary>
    /// Resolve a numeric world ID (e.g. 21) to its in-game name (e.g. "Cactuar")
    /// via Lumina's World sheet. Cached. Falls back to "World N" when the sheet
    /// is unavailable, the row is missing, or the name is blank — never returns
    /// null/empty so display sites don't have to special-case.
    /// </summary>
    public string LookupWorldName(uint worldId) => LookupWorld(worldId).Name;

    /// <summary>
    /// Resolve a numeric world ID to its data-center name (e.g. "Aether"). Empty
    /// string when the DC link is missing or the world is unknown.
    /// </summary>
    public string LookupDataCenterName(uint worldId) => LookupWorld(worldId).DataCenter;

    private WorldInfo LookupWorld(uint worldId)
    {
        if (worldInfoCache.TryGetValue(worldId, out var cached))
            return cached;

        var resolved = ResolveWorld(worldId);
        worldInfoCache[worldId] = resolved;
        return resolved;
    }

    private readonly Dictionary<uint, WorldInfo> worldInfoCache = new();

    /// <summary>Cached snapshot of a world row: friendly name + data-center name.</summary>
    private readonly record struct WorldInfo(string Name, string DataCenter);

    private static WorldInfo ResolveWorld(uint worldId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<World>();
            if (sheet == null) return new WorldInfo($"World {worldId}", "");

            var row = sheet.GetRowOrDefault(worldId);
            if (row == null) return new WorldInfo($"World {worldId}", "");

            var name = row.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) name = $"World {worldId}";

            // DataCenter is a row link to WorldDCGroupType. Empty string when the
            // row's DC pointer is null (e.g. test / placeholder worlds), which
            // FormatPlotKeyLocation handles gracefully by omitting the parens.
            var dc = row.Value.DataCenter.ValueNullable?.Name.ExtractText() ?? "";

            return new WorldInfo(name, dc);
        }
        catch
        {
            return new WorldInfo($"World {worldId}", "");
        }
    }

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
