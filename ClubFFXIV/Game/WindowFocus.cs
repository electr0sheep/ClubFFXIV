using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClubFFXIV.Game;

internal static class WindowFocus
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // MainWindowHandle resolution iterates over every window of every thread
    // in the process — too expensive to do per-frame. The handle never changes
    // for the lifetime of the FFXIV process, so resolve it lazily on first use
    // and cache it forever after.
    private static IntPtr cachedGameWindow;

    /// <summary>
    /// Returns true when FFXIV's main window is the foreground window
    /// (game has input focus, not minimized, not behind another window).
    /// </summary>
    public static bool IsGameFocused()
    {
        if (cachedGameWindow == IntPtr.Zero)
            cachedGameWindow = Process.GetCurrentProcess().MainWindowHandle;
        return GetForegroundWindow() == cachedGameWindow;
    }
}
