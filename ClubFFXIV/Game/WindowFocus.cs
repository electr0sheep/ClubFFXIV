using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClubFFXIV.Game;

internal static class WindowFocus
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Returns true when FFXIV's main window is the foreground window
    /// (game has input focus, not minimized, not behind another window).
    /// </summary>
    public static bool IsGameFocused()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var ours = Process.GetCurrentProcess().MainWindowHandle;
        return fg == ours;
    }
}
