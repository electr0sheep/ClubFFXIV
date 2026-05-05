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

    public void Mute()
    {
        if (originalState.HasValue) return; // already muted by us

        try
        {
            var current = Plugin.GameConfig.System.TryGet(Option, out bool wasMuted) && wasMuted;
            originalState = current;
            Plugin.GameConfig.System.Set(Option, true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"BGM mute failed: {ex.Message}");
        }
    }

    public void Unmute()
    {
        if (!originalState.HasValue) return;

        try
        {
            Plugin.GameConfig.System.Set(Option, originalState.Value);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"BGM unmute failed: {ex.Message}");
        }
        finally
        {
            originalState = null;
        }
    }

    public void Dispose() => Unmute();
}
