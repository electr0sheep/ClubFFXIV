using System;

namespace ClubFFXIV.Game;

/// <summary>
/// Stable, hashable identifier for a single FFXIV house instance.
/// Composed from world + housing district + ward + plot + room (apartments) + division (subdivision flag).
/// Serialized as a colon-delimited string for use as a JSON dictionary key.
/// </summary>
public readonly record struct PlotKey(
    uint WorldId,
    uint TerritoryType,
    int Ward,
    int Plot,
    int Room,
    int Division)
{
    public string Canonical => $"{WorldId}:{TerritoryType}:{Ward}:{Plot}:{Room}:{Division}";

    public override string ToString() => Canonical;

    public static bool TryParse(string s, out PlotKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split(':');
        if (parts.Length != 6) return false;
        if (!uint.TryParse(parts[0], out var w)) return false;
        if (!uint.TryParse(parts[1], out var t)) return false;
        if (!int.TryParse(parts[2], out var ward)) return false;
        if (!int.TryParse(parts[3], out var plot)) return false;
        if (!int.TryParse(parts[4], out var room)) return false;
        if (!int.TryParse(parts[5], out var div)) return false;
        key = new PlotKey(w, t, ward, plot, room, div);
        return true;
    }
}
