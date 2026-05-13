using System;

namespace ClubFFXIV.Audio;

/// <summary>
/// Mutes FFXIV's BGM by toggling the game's own "IsSndBgm" config option through
/// Dalamud's IGameConfig service. This is surgical — only BGM is silenced, SFX,
/// voices, and our stream output are unaffected.
///
/// Replaces the earlier Windows-audio-session approach which would have muted
/// our own stream too (we share the ffxiv_dx11.exe audio session).
/// </summary>
public sealed class GameBgmMuter : IDisposable
{
    private const string Option = "IsSndBgm";
    private bool? originalState;
    private bool selfInitiated;

    public void Mute()
    {
        if (originalState.HasValue) return; // already muted by us

        try
        {
            var current = Plugin.GameConfig.System.TryGet(Option, out bool wasMuted) && wasMuted;
            originalState = current;
            selfInitiated = true;
            Plugin.GameConfig.System.Set(Option, true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"BGM mute failed: {ex.Message}");
        }
        finally
        {
            selfInitiated = false;
        }
    }

    public void Unmute()
    {
        if (!originalState.HasValue) return;

        try
        {
            selfInitiated = true;
            Plugin.GameConfig.System.Set(Option, originalState.Value);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"BGM unmute failed: {ex.Message}");
        }
        finally
        {
            originalState = null;
            selfInitiated = false;
        }
    }

    /// <summary>
    /// Called when IGameConfig.SystemChanged fires for IsSndBgm. If we're
    /// currently muting and the change wasn't from our own Set, re-snapshot
    /// the user's new preference so the eventual Unmute restores to what
    /// they actually want — not the stale value captured at first mute.
    /// </summary>
    public void OnExternalChange()
    {
        if (selfInitiated || !originalState.HasValue) return;

        try
        {
            if (Plugin.GameConfig.System.TryGet(Option, out bool current))
                originalState = current;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"BGM re-snapshot failed: {ex.Message}");
        }
    }

    public void Dispose() => Unmute();
}
