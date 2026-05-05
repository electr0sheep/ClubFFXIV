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
        Vector3 DoorPosition);

    public readonly record struct Result(
        Candidate Candidate,
        float Distance,
        float NormalizedNearness);

    /// <summary>
    /// Returns the closest candidate to playerPos that has a door in the same ward,
    /// or null if no candidate is within audibleRange.
    /// NormalizedNearness is 0 at audibleRange and 1 at fullRange.
    /// </summary>
    public static Result? FindClosest(
        Vector3 playerPos,
        IEnumerable<Candidate> candidates,
        float audibleRange,
        float fullRange)
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

        if (!found || bestDist > audibleRange) return null;

        var nearness = Normalize(bestDist, audibleRange, fullRange);
        return new Result(best, bestDist, nearness);
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
}
