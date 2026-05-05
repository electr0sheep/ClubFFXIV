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
        float NormalizedNearness,
        bool Audible,    // within audibleRange — user actually hears it
        bool Streaming); // within streamRange — keep the stream alive (may be silent if buffering)

    /// <summary>
    /// Returns the closest candidate to playerPos (regardless of range), or null
    /// if no candidates exist at all.
    ///
    /// Two range thresholds:
    ///   - streamRange: connect/keep the stream alive within this distance, even
    ///     if silent. Hides the 1–3s buffer wait by pre-loading before audio kicks in.
    ///   - audibleRange: actual volume curve goes 0→1 from audibleRange to fullRange.
    ///     Outside audibleRange but inside streamRange = pre-buffering, volume 0.
    /// </summary>
    public static Result? FindClosest(
        Vector3 playerPos,
        IEnumerable<Candidate> candidates,
        float streamRange,
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

        if (!found) return null;

        var streaming = bestDist <= streamRange;
        var audible = bestDist <= audibleRange;
        var nearness = Normalize(bestDist, audibleRange, fullRange);
        return new Result(best, bestDist, nearness, audible, streaming);
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
