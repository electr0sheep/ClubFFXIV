using System;
using System.Numerics;

namespace ClubFFXIV.Game;

/// <summary>
/// Resolves the listener's horizontal facing for spatial-audio panning.
/// Currently sourced from the local player's character rotation — a reliable
/// scalar exposed by Dalamud across all game patches. A camera-relative
/// override (so orbiting the camera around a stationary character re-pans
/// the audio) is a planned follow-up; the FFXIVClientStructs camera fields
/// drift across game versions, so we'd need a per-version offset map first.
/// </summary>
public static class ListenerOrientationProvider
{
    /// <summary>
    /// Best-effort horizontal orientation. Returns null when no character is
    /// loaded (cutscene transitions, very early plugin load, etc.) — callers
    /// should treat that as "skip the directional cue this tick" rather than
    /// erroring.
    /// </summary>
    public static WardProximity.ListenerOrientation? Get(bool invertPan)
    {
        var ori = TryFromPlayer();
        if (ori == null) return null;
        if (invertPan)
        {
            var o = ori.Value;
            return new WardProximity.ListenerOrientation(o.Forward, -o.Right);
        }
        return ori;
    }

    private static WardProximity.ListenerOrientation? TryFromPlayer()
    {
        try
        {
            var pc = Plugin.ObjectTable.LocalPlayer;
            if (pc == null) return null;
            // FFXIV character rotation: scalar yaw in radians, 0 = facing south
            // (+Z). Same convention is used by every angle-to-target helper
            // across the Dalamud plugin ecosystem.
            var yaw = pc.Rotation;
            var forward = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
            // Right axis is the horizontal perpendicular. Sign convention is
            // "world-space cross of up with forward" — if it ends up flipped
            // on a particular FFXIV coord-system reading, the user-facing
            // "Invert L/R" toggle compensates without a recompile.
            var right = new Vector3(forward.Z, 0, -forward.X);
            return new WardProximity.ListenerOrientation(forward, right);
        }
        catch
        {
            return null;
        }
    }
}
