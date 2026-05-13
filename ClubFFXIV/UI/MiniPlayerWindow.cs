using System;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// Ultra-compact alternative to <see cref="MusicPlayerWindow"/>. Renders
/// just the Now Playing strip — thumbnail, mute, play/pause, skip-next,
/// label — without the URL-paste popup, transport seek bar, volume slider,
/// or proximity readout. Intended as a Manual-mode HUD for a listener
/// playing a specific stream by hand; the full player (where you add
/// streams, change volume, see proximity) is one swap away via the
/// title-bar "expand" button or <c>/pclub</c>.
///
/// <para>Visibility is gated by playback mode: <see cref="DrawConditions"/>
/// suppresses rendering whenever <see cref="Plugin.CurrentMode"/> is not
/// <see cref="PlaybackMode.Manual"/>. <see cref="Window.IsOpen"/> still
/// reflects the user's open/close intent, so the window reappears
/// automatically when mode returns to Manual without the user having to
/// reopen it.</para>
/// </summary>
public sealed class MiniPlayerWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    /// <summary>
    /// Mode gate: the mini is the Manual-mode HUD, so we suppress rendering
    /// in every other mode (Off / Indoor / Outdoor). Returning false here
    /// leaves <see cref="Window.IsOpen"/> untouched — when CurrentMode flips
    /// back to Manual on the next frame, the window reappears with the same
    /// open-state it had before.
    /// </summary>
    public override bool DrawConditions() =>
        plugin.CurrentMode == PlaybackMode.Manual;

    public MiniPlayerWindow(Plugin plugin)
        : base("ClubFFXIV — Mini##ClubFFXIVMiniPlayer",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        // Compact default — one row plus a little breathing room. User can
        // resize to taste; FirstUseEver keeps a hand-tuned size sticky.
        Size = new Vector2(320, 72);
        SizeCondition = ImGuiCond.FirstUseEver;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Expand,
            Click = _ => plugin.SwapToMusicPlayer(),
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Switch to full player");
                ImGui.EndTooltip();
            },
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        var entries = plugin.GetNowPlayingEntries();

        if (entries.Count == 0)
        {
            // Same "Not playing" placeholder shape as the music player — a
            // pause glyph plus a label — so the window has visible content
            // when nothing is streaming.
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
            ImGui.TextColored(UiColors.IconPlaceholder, FontAwesomeIcon.Pause.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextUnformatted("Not playing");
            return;
        }

        // Thumbnail height tracks the icon row at the music player's 1.6×
        // FrameHeight so artwork sits flush with the control glyphs.
        var thumbSize = ImGui.GetFrameHeight() * 1.6f;
        var showThumbs = plugin.Config.ShowNowPlayingThumbnails;

        foreach (var entry in entries)
            DrawRow(entry, showThumbs ? thumbSize : 0f);
    }

    private void DrawRow(Plugin.NowPlayingEntry entry, float thumbSize)
    {
        ImGui.PushID("mini-" + entry.Url);

        if (thumbSize > 0f)
        {
            NowPlayingThumbnails.Draw(
                entry.ThumbnailUrl, entry.CropThumbToSquare,
                new Vector2(thumbSize, thumbSize));
            ImGui.SameLine();
        }

        var muted = entry.Muted;
        var muteIcon = muted ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
        UiHelpers.IconClickable(muteIcon,
            muted ? "Click to unmute" : "Click to mute",
            () => plugin.ToggleNowPlayingMute(entry));

        // Live entries omit play/pause and skip — same gating rule the full
        // player applies, kept local here so a stale entry can't drive a
        // no-op action against a live source.
        if (!entry.IsLive)
        {
            ImGui.SameLine();
            var playIcon = entry.IsPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
            UiHelpers.IconClickable(playIcon,
                entry.IsPaused ? "Click to play" : "Click to pause",
                () => plugin.ToggleNowPlayingPause(entry),
                alignToFramePadding: false);
        }

        if (entry.CanSkipNext)
        {
            ImGui.SameLine();
            UiHelpers.IconClickable(FontAwesomeIcon.AngleDoubleRight,
                "Skip to next track",
                () => plugin.SkipNowPlayingToNext(entry),
                alignToFramePadding: false);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(entry.Label);
        if (entry.Label != entry.Url && ImGui.IsItemHovered())
            ImGui.SetTooltip(entry.Url);

        ImGui.PopID();
    }
}
