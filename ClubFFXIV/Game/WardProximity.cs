using System;
using System.Collections.Generic;
using System.Numerics;

namespace ClubFFXIV.Game;

/// <summary>
/// Computes proximity to calibrated clubs in the player's current ward.
/// Pure logic — no Dalamud or NAudio dependencies, easy to reason about.
/// </summary>
public static class WardProximity
{
    public readonly record struct Candidate(
        string CanonicalKey,
        string DisplayName,
        string StreamUrl,
        Vector3 DoorPosition,
        string Description);

    /// <summary>
    /// Listener (player/camera) facing in world space, projected onto the
    /// horizontal plane. Used to convert a door's world position into a
    /// listener-relative pan + rearness pair. Both vectors are unit length
    /// and have Y=0 — vertical separation isn't reproducible over stereo
    /// anyway, so we deliberately collapse to 2D.
    /// </summary>
    public readonly record struct ListenerOrientation(Vector3 Forward, Vector3 Right);

    public readonly record struct Result(
        Candidate Candidate,
        float Distance,
        float NormalizedNearness,
        bool Audible,    // within audibleRange — user actually hears it
        bool Streaming,  // within streamRange — keep the stream alive (may be silent if buffering)
        float Pan,       // -1 = full left, 0 = centered, +1 = full right
        float Rearness); // 0 = directly in front, 1 = directly behind

    /// <summary>
    /// Returns the closest candidate to playerPos (regardless of range), or null
    /// if no candidates exist at all.
    ///
    /// Two range thresholds:
    ///   - streamRange: connect/keep the stream alive within this distance, even
    ///     if silent. Hides the 1–3s buffer wait by pre-loading before audio kicks in.
    ///   - audibleRange: actual volume curve goes 0→1 from audibleRange to fullRange.
    ///     Outside audibleRange but inside streamRange = pre-buffering, volume 0.
    ///
    /// When <paramref name="orientation"/> is null, Pan and Rearness on the result
    /// are 0 — i.e. the directional cue is bypassed (centered, treated as "in front").
    /// </summary>
    public static Result? FindClosest(
        Vector3 playerPos,
        IEnumerable<Candidate> candidates,
        float streamRange,
        float audibleRange,
        float fullRange,
        ListenerOrientation? orientation = null)
    {
        Candidate best = default;
        float bestDist = float.MaxValue;
        bool found = false;

        foreach (var c in candidates)
        {
            var d = Vector3.Distance(playerPos, c.DoorPosition);
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
                found = true;
            }
        }

        if (!found) return null;

        var streaming = bestDist <= streamRange;
        var audible = bestDist <= audibleRange;
        var nearness = Normalize(bestDist, audibleRange, fullRange);
        var (pan, rearness) = ComputeDirection(playerPos, best.DoorPosition, orientation);
        return new Result(best, bestDist, nearness, audible, streaming, pan, rearness);
    }

    /// <summary>
    /// Returns every candidate within streamRange, sorted nearest-first. Used by
    /// the multi-stream code path so each in-range plot can host its own voice in
    /// the mixer. Out-of-range candidates are dropped (unlike FindClosest, which
    /// reports the closest one regardless of range).
    /// </summary>
    public static List<Result> FindAllInRange(
        Vector3 playerPos,
        IEnumerable<Candidate> candidates,
        float streamRange,
        float audibleRange,
        float fullRange,
        ListenerOrientation? orientation = null)
    {
        var results = new List<Result>();
        foreach (var c in candidates)
        {
            var d = Vector3.Distance(playerPos, c.DoorPosition);
            if (d > streamRange) continue;
            var nearness = Normalize(d, audibleRange, fullRange);
            var (pan, rearness) = ComputeDirection(playerPos, c.DoorPosition, orientation);
            results.Add(new Result(c, d, nearness, d <= audibleRange, true, pan, rearness));
        }
        results.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return results;
    }

    /// <summary>
    /// Returns 0 at far, 1 at near. Clamped.
    /// </summary>
    public static float Normalize(float distance, float far, float near)
    {
        if (distance <= near) return 1f;
        if (distance >= far) return 0f;
        return 1f - (distance - near) / (far - near);
    }

    /// <summary>
    /// Maps nearness (0..1) to a frequency between minHz and maxHz on a perceptual
    /// (logarithmic) scale — sounds more natural than linear interpolation.
    /// </summary>
    public static float NearnessToCutoff(float nearness, float minHz, float maxHz)
    {
        var t = System.Math.Clamp(nearness, 0f, 1f);
        var logMin = System.Math.Log(minHz);
        var logMax = System.Math.Log(maxHz);
        return (float)System.Math.Exp(logMin + t * (logMax - logMin));
    }

    /// <summary>
    /// Bend the lowpass cutoff downward when the door is behind the listener,
    /// approximating the spectral shadow that real ears use as a front/back
    /// cue. <paramref name="rearness"/> is 0 in front, 1 directly behind;
    /// <paramref name="strength"/> is 0..1 (0 = no rear muffle, 1 = halve the
    /// cutoff at full rear).
    /// </summary>
    public static float ApplyRearMuffle(float cutoffHz, float rearness, float strength)
    {
        var s = System.Math.Clamp(strength, 0f, 1f);
        var r = System.Math.Clamp(rearness, 0f, 1f);
        // 0 rearness → factor 1 (unchanged); 1 rearness → factor (1 - s/2).
        var factor = 1f - 0.5f * s * r;
        return cutoffHz * factor;
    }

    /// <summary>
    /// Project the door's horizontal offset from the player onto the listener's
    /// right and forward axes to get pan ∈ [-1,+1] and rearness ∈ [0,1].
    /// Vertical (Y) component is dropped — stereo can't reproduce height anyway.
    /// Public so the per-frame spatial-pose refresh can recompute pan from a
    /// cached door position without re-running the whole proximity sweep.
    /// </summary>
    public static (float Pan, float Rearness) ComputeDirection(
        Vector3 playerPos, Vector3 doorPos, ListenerOrientation? orientation)
    {
        if (orientation == null) return (0f, 0f);
        var o = orientation.Value;

        var delta = doorPos - playerPos;
        delta.Y = 0;
        var len = delta.Length();
        // Standing on top of the door — no meaningful direction.
        if (len < 1e-4f) return (0f, 0f);
        delta /= len;

        var pan = Vector3.Dot(delta, o.Right);
        var fwdDot = Vector3.Dot(delta, o.Forward);
        var rearness = System.Math.Clamp(-fwdDot, 0f, 1f);
        return (System.Math.Clamp(pan, -1f, 1f), rearness);
    }
}
