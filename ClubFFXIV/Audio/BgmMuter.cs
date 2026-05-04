using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace ClubFFXIV.Audio;

/// <summary>
/// Phase 1 approach: mute the entire FFXIV audio session via Windows Core Audio.
/// Side effect: this also mutes SFX, voices, and ambient sound — not just BGM.
///
/// Phase 0 spike outcome will determine whether we upgrade to:
///   - sigscan the BGM volume slider for surgical mute (BGM only, fragile across patches)
///   - hook the game's audio mixer (most precise, most maintenance burden)
/// </summary>
public sealed class BgmMuter : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private bool wasMutedBefore;
    private bool weMutedIt;

    public void Mute()
    {
        var session = FindGameSession();
        if (session == null) return;

        wasMutedBefore = session.SimpleAudioVolume.Mute;
        session.SimpleAudioVolume.Mute = true;
        weMutedIt = true;
    }

    public void Unmute()
    {
        if (!weMutedIt) return;

        var session = FindGameSession();
        if (session == null) return;

        // Restore prior state — if the user had already muted FFXIV before we touched it,
        // leave it muted.
        session.SimpleAudioVolume.Mute = wasMutedBefore;
        weMutedIt = false;
    }

    private AudioSessionControl? FindGameSession()
    {
        // We're injected into ffxiv_dx11.exe, so our own PID *is* the game's PID.
        var pid = (uint)Process.GetCurrentProcess().Id;

        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.GetProcessID == pid)
                return session;
        }
        return null;
    }

    public void Dispose()
    {
        try { Unmute(); } catch { /* nothing useful to do during teardown */ }
        enumerator.Dispose();
    }
}
