using System;
using System.Collections.Generic;
using System.Numerics;
using ClubFFXIV.Audio;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// Compact "music player" window — the primary listener-facing surface.
/// Shows the Now Playing header (with per-row transport controls), the
/// URL/Play/Stop section, and the proximity status readout. The full
/// settings UI (My Clubs, Registry, Settings, Advanced) lives in
/// <see cref="ConfigWindow"/>; this window's title bar carries a gear
/// button that toggles it.
/// </summary>
public sealed class MusicPlayerWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string urlInput;

    // Per-row "user is currently dragging the seek slider" state, keyed by
    // entry URL. Present iff the user has the slider grabbed — while present,
    // the slider's displayed value follows the cursor instead of the live
    // playback position (which would otherwise yank the thumb out from under
    // the user every frame). Cleared on release in DrawNowPlayingRow.
    private readonly Dictionary<string, float> seekDragValues = new();

    // Set by the title-bar "+" button's click handler; consumed on the next
    // Draw() to call OpenPopup. Routed through this flag (rather than calling
    // OpenPopup directly inside the click handler) because the title-bar
    // button render runs outside this window's id stack — OpenPopup needs to
    // resolve to the same scope as the matching BeginPopup, which only
    // happens during Draw.
    private bool openAddStreamRequested;

    public MusicPlayerWindow(Plugin plugin)
        : base("ClubFFXIV — Music Player##ClubFFXIVMusicPlayer", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        // Compact default — user resizes to taste. Enough vertical space for
        // the Now Playing header (1–2 rows), URL input, Volume slider, and
        // proximity status. ConfigWindow keeps the wider 600x720 footprint.
        Size = new Vector2(440, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        urlInput = plugin.Config.LastStreamUrl;

        // Title-bar buttons render right-to-left next to the close button, in
        // the order they're added. Order from leftmost to rightmost (as the
        // user sees them): [+] [?] [⚙] [⤓] [×]. The plus opens an "add stream"
        // popup so the URL field doesn't permanently sit in the player body;
        // the compress glyph swaps over to MiniPlayerWindow.
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Plus,
            Click = _ =>
            {
                // Pre-fill the input with the persisted last URL so a returning
                // user can just hit Play. Always refresh on open in case the
                // value changed via /pclub play <url> from chat.
                urlInput = plugin.Config.LastStreamUrl;
                openAddStreamRequested = true;
            },
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Add stream");
                ImGui.EndTooltip();
            },
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.QuestionCircle,
            Click = _ => plugin.ToggleHelp(),
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Getting Started");
                ImGui.EndTooltip();
            },
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Click = _ => plugin.ToggleConfig(),
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Settings");
                ImGui.EndTooltip();
            },
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Compress,
            Click = _ => plugin.SwapToMiniPlayer(),
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Switch to mini player");
                ImGui.EndTooltip();
            },
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawFirstRunBanner();
        DrawNowPlayingHeader();
        ImGui.Spacing();
        DrawPlayerControls();

        // Playback mode + proximity readout is gated behind a setting — by
        // default the player stays focused on what's actually playing. The
        // separator is drawn as part of the same gate so we don't leave an
        // orphan rule above empty space when the section is hidden.
        if (plugin.Config.ShowPlaybackModeAndProximity)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawProximityStatusSection();
        }

        // The "+" title-bar click handler can't open the popup itself — it
        // runs outside this window's id stack. We forward the intent here.
        if (openAddStreamRequested)
        {
            ImGui.OpenPopup("##addStream");
            openAddStreamRequested = false;
        }
        DrawAddStreamPopup();
    }

    /// <summary>
    /// Onboarding nudge shown only on the very first launch (no last-stream
    /// URL persisted). The always-visible help affordance is a question-mark
    /// button in the title bar — added in the constructor — so the banner
    /// disappears as soon as the user plays anything.
    /// </summary>
    private void DrawFirstRunBanner()
    {
        if (!string.IsNullOrEmpty(plugin.Config.LastStreamUrl)) return;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiColors.BannerInfo);
        ImGui.BeginChild("##firstRunBanner", new Vector2(-1, 56), true);
        ImGui.Spacing();
        ImGui.TextWrapped("First time? Click \"Getting Started\" for a 30-second walkthrough.");
        ImGui.Spacing();
        if (ImGui.Button("Getting Started"))
            plugin.ToggleHelp();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void DrawNowPlayingHeader()
    {
        var entries = plugin.GetNowPlayingEntries();
        var showThumbs = plugin.Config.ShowNowPlayingThumbnails && entries.Count > 0;

        // Auto-size the dark strip to fit one row per active stream (or a
        // single placeholder row when nothing is playing). NoScrollbar keeps
        // it from showing a sidebar — if for some reason the content overflows
        // the parent, rows just clip rather than introduce a scroll affordance.
        // Thumbnail rows are taller — anchor row height to the thumbnail size
        // so the artwork has square pixels and the row chrome (mute icon,
        // label, blacklist button) vertically centres against it.
        var thumbSize = ImGui.GetFrameHeight() * 1.6f;
        var baseRowH = showThumbs
            ? thumbSize + ImGui.GetStyle().ItemSpacing.Y
            : ImGui.GetFrameHeightWithSpacing();
        // Non-live rows get a second line for the seek bar + timestamp;
        // anchor to FrameHeightWithSpacing so it sizes consistently
        // regardless of whether thumbnails are shown.
        var transportExtraH = ImGui.GetFrameHeightWithSpacing();

        float contentH;
        if (entries.Count == 0)
        {
            contentH = baseRowH;
        }
        else
        {
            contentH = 0f;
            foreach (var e in entries)
            {
                contentH += baseRowH;
                if (HasTransportRow(e)) contentH += transportExtraH;
            }
        }
        var height = contentH + 12f;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiColors.PanelDark);
        ImGui.BeginChild("##nowPlayingHeader", new Vector2(-1, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        if (entries.Count == 0)
        {
            // Static placeholder — no playback to act on.
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
            ImGui.TextColored(UiColors.IconPlaceholder, FontAwesomeIcon.Pause.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextUnformatted("Not playing");
        }
        else
        {
            foreach (var entry in entries)
                DrawNowPlayingRow(entry, showThumbs ? thumbSize : 0f);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// True iff the row should render a second line for transport controls
    /// (seek bar / timestamp). Non-live entries always show at least a
    /// position readout; live entries only get the existing single-line
    /// chrome (mute + label + blacklist).
    /// </summary>
    private static bool HasTransportRow(Plugin.NowPlayingEntry e) => !e.IsLive;

    private void DrawNowPlayingRow(Plugin.NowPlayingEntry entry, float thumbSize)
    {
        ImGui.PushID("np-" + entry.Url);

        if (thumbSize > 0f)
        {
            // Thumbnail goes at the row start; subsequent SameLine calls put
            // the mute icon and label to its right. The cursor advance for
            // both the loaded image and the placeholder is exactly thumbSize
            // square so loading-state transitions don't shift the layout.
            NowPlayingThumbnails.Draw(
                entry.ThumbnailUrl, entry.CropThumbToSquare,
                new Vector2(thumbSize, thumbSize));
            ImGui.SameLine();
        }

        // The icon itself is the mute/unmute click target. Action-based
        // affordance: when audible, show a mute symbol (clicking silences);
        // when muted, show the green play button (clicking resumes).
        var muted = entry.Muted;
        var muteIcon = muted ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
        UiHelpers.IconClickable(muteIcon,
            muted ? "Click to unmute" : "Click to mute",
            () => plugin.ToggleNowPlayingMute(entry));

        // Play/Pause icon for non-live rows. Live rows omit this entirely —
        // the mute chrome is the only meaningful control there.
        if (!entry.IsLive)
        {
            ImGui.SameLine();
            var playIcon = entry.IsPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
            UiHelpers.IconClickable(playIcon,
                entry.IsPaused ? "Click to play" : "Click to pause",
                () => plugin.ToggleNowPlayingPause(entry),
                alignToFramePadding: false);
        }

        // Skip-next icon — only for sources that can advance (yt-dlp playlist
        // wrappers, including single-video URLs which exhaust on click and
        // hand off to the natural-end loop logic).
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
        // Full URL on hover, in case the truncation hid the relevant tail.
        if (entry.Label != entry.Url && ImGui.IsItemHovered())
            ImGui.SetTooltip(entry.Url);

        // Right-aligned Blacklist button — same FontAwesomeIcon.Ban glyph
        // the My Clubs tab uses for Unpublish, so the row chrome stays
        // compact and the affordance reads consistently across windows.
        // Width measured under the icon font so the right-align math
        // reflects glyph metrics, not the regular text font.
        ImGui.SameLine();
        DrawBlacklistIconButton(entry);

        // Second line: seek bar + timestamp for non-live entries. The
        // header's height calculation reserves space for this so it doesn't
        // overflow the bordered child window.
        if (HasTransportRow(entry))
            DrawNowPlayingTransport(entry, thumbSize);

        ImGui.PopID();
    }

    /// <summary>
    /// Right-aligned Ban-icon button that blacklists the row's URL — same
    /// glyph the My Clubs tab uses for Unpublish, so the affordance reads
    /// consistently. Width is measured under the icon font so the right-align
    /// math reflects glyph metrics, not the regular text font.
    /// </summary>
    private void DrawBlacklistIconButton(Plugin.NowPlayingEntry entry)
    {
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        var iconW = ImGui.CalcTextSize(FontAwesomeIcon.Ban.ToIconString()).X
                    + ImGui.GetStyle().FramePadding.X * 2 + 4;
        ImGui.PopFont();
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - iconW);

        if (UiHelpers.IconSmallButton(FontAwesomeIcon.Ban,
            "blacklist-" + entry.Url, "Blacklist this stream"))
        {
            plugin.BlacklistNowPlaying(entry);
        }
    }

    /// <summary>
    /// Render the second-line transport widgets for a non-live entry: a
    /// seek slider (if duration is known) plus an "MM:SS / MM:SS" timestamp.
    /// Indented to line up under the label so the seek bar visually belongs
    /// to its row even with a thumbnail above. Drag-vs-playback display is
    /// disambiguated via <see cref="seekDragValues"/> — while the user holds
    /// the slider, the displayed value follows the cursor; otherwise it
    /// follows the live playback position.
    /// </summary>
    private void DrawNowPlayingTransport(Plugin.NowPlayingEntry entry, float thumbSize)
    {
        // Indent so the second line lines up with the label, leaving the
        // thumbnail (if any) and the icons-column to the left.
        var indent = thumbSize > 0
            ? thumbSize + ImGui.GetStyle().ItemSpacing.X
            : 0f;
        if (indent > 0) ImGui.Indent(indent);

        var pos = (float)entry.PositionSeconds;
        var dur = (float)entry.DurationSeconds;

        if (entry.Seekable && dur > 0)
        {
            // Reserve space on the right for the timestamp so the slider
            // takes whatever's left. CalcTextSize the longest possible
            // string (duration / duration) so the layout doesn't shift as
            // pos ticks up to one more digit.
            var tsText = $"{FormatTime(pos)} / {FormatTime(dur)}";
            var tsLongest = $"{FormatTime(dur)} / {FormatTime(dur)}";
            var tsWidth = ImGui.CalcTextSize(tsLongest).X + ImGui.GetStyle().ItemSpacing.X;
            var sliderWidth = ImGui.GetContentRegionAvail().X - tsWidth - 4;
            if (sliderWidth < 60) sliderWidth = 60;

            // Resolve the displayed slider value: if the user is mid-drag
            // we have a stored value and use it; otherwise the live position
            // wins. Without this split the live-position update each frame
            // would yank the slider thumb out from under the dragging cursor.
            float val = seekDragValues.TryGetValue(entry.Url, out var heldVal)
                ? heldVal
                : pos;

            ImGui.SetNextItemWidth(sliderWidth);
            ImGui.SliderFloat("##seek", ref val, 0f, dur, "");

            // While the slider is being held, remember the drag value for
            // the next frame. Once released and edited, commit the seek and
            // forget — subsequent frames fall back to live playback.
            if (ImGui.IsItemActive())
            {
                seekDragValues[entry.Url] = val;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                plugin.SeekNowPlaying(entry, val);
                seekDragValues.Remove(entry.Url);
            }
            else if (!ImGui.IsItemActive())
            {
                // Idle slider — drop any stale drag entry so a future
                // playback-driven update flows through normally.
                seekDragValues.Remove(entry.Url);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(tsText);
        }
        else
        {
            // Non-live but no duration / non-seekable source — just show the
            // elapsed timestamp. Pause still works (rendered as the play/pause
            // icon on the first line); seek isn't available, so no slider.
            ImGui.TextUnformatted(dur > 0
                ? $"{FormatTime(pos)} / {FormatTime(dur)}"
                : FormatTime(pos));
        }

        if (indent > 0) ImGui.Unindent(indent);
    }

    /// <summary>
    /// Format a time in seconds as MM:SS, or H:MM:SS for sources past an
    /// hour (rare for music videos but possible for long-form mixes / DJ
    /// sets). Negative or NaN inputs render as "--:--" so the row still
    /// has a stable width while metadata is loading.
    /// </summary>
    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return "--:--";
        var total = (int)Math.Floor(seconds);
        var hours = total / 3600;
        var minutes = (total % 3600) / 60;
        var secs = total % 60;
        return hours > 0
            ? $"{hours}:{minutes:D2}:{secs:D2}"
            : $"{minutes:D2}:{secs:D2}";
    }

    /// <summary>
    /// Always-on player controls: Stop, Loop toggle, Random toggle, Volume.
    /// The URL paste UI (with hint, description, and Play button) lives in
    /// the popup opened by the title-bar "+" — see
    /// <see cref="DrawAddStreamPopup"/>. Keeping Stop in the body means the
    /// user can halt the current stream without first opening the popup,
    /// which is the common case after walking away from a club.
    /// </summary>
    private void DrawPlayerControls()
    {
        DrawStopIcon();
        ImGui.SameLine();
        DrawLoopToggle();
        ImGui.SameLine();
        DrawRandomToggle();

        ImGui.Spacing();
        DrawVolumeRow();
    }

    /// <summary>
    /// Volume slider with a FontAwesome glyph instead of a "Volume" label.
    /// The icon switches across three states so the row carries volume
    /// state at a glance — zero, low, high — even when the user can't read
    /// the slider value precisely. Slider takes the remaining width via
    /// SetNextItemWidth(-1) so the row stays one-line on narrow windows;
    /// the inline "%.2f" overlay sits inside the bar where ImGui draws it.
    /// </summary>
    private void DrawVolumeRow()
    {
        var volume = plugin.Config.Volume;

        // Bands: exact-zero is "off"; below the midpoint is "down"; at-or-
        // above midpoint is "up". The 0.001 threshold absorbs float-rounding
        // when the user drags to the leftmost stop and the slider lands a
        // hair off zero.
        var icon = volume <= 0.001f
            ? FontAwesomeIcon.VolumeOff
            : volume < 0.5f
                ? FontAwesomeIcon.VolumeDown
                : FontAwesomeIcon.VolumeUp;

        ImGui.AlignTextToFramePadding();
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        ImGui.TextUnformatted(icon.ToIconString());
        ImGui.PopFont();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##volume", ref volume, 0f, 1f, "%.2f"))
        {
            plugin.Config.Volume = volume;
            plugin.Config.Save();
            plugin.SetStreamVolume(volume);
        }
    }

    private void DrawStopIcon()
    {
        UiHelpers.IconClickable(FontAwesomeIcon.Stop, "Stop", plugin.StopStream);
    }

    /// <summary>
    /// Loop toggle: when a finite source ends, restart it. Active = bright
    /// icon; inactive = dim.
    /// </summary>
    private void DrawLoopToggle()
    {
        var active = plugin.Config.LoopFinishedVideos;
        UiHelpers.IconClickable(
            FontAwesomeIcon.Repeat,
            (active ? "Loop: ON\n" : "Loop: OFF\n") +
            "When a finite source ends, restart it from the top:\n" +
            "  • Single video: replays the same video.\n" +
            "  • Playlist: starts the playlist over (with a fresh\n" +
            "    shuffle if Random is on).\n" +
            "Off = playlist plays through once and stops.\n" +
            "Indefinite streams (Twitch, Icecast) never reach a\n" +
            "natural end, so this setting is a no-op for them.",
            () =>
            {
                plugin.Config.LoopFinishedVideos = !active;
                plugin.Config.Save();
            },
            color: active ? UiColors.IconActive : UiColors.IconInactive);
    }

    /// <summary>
    /// Random playlist order toggle. Forwarded to <see cref="Plugin.SyncYtDlpOptions"/>
    /// so the next play picks up the new flag — already-running yt-dlp
    /// processes don't re-shuffle mid-stream.
    /// </summary>
    private void DrawRandomToggle()
    {
        var active = plugin.Config.PlaylistRandom;
        UiHelpers.IconClickable(
            FontAwesomeIcon.Random,
            (active ? "Random order: ON\n" : "Random order: OFF\n") +
            "How playlist items are ordered:\n" +
            "  • Off: original playlist order.\n" +
            "  • On: yt-dlp shuffles the playlist before iteration.\n" +
            "No effect on single videos or live streams.",
            () =>
            {
                plugin.Config.PlaylistRandom = !active;
                plugin.Config.Save();
                plugin.SyncYtDlpOptions();
            },
            color: active ? UiColors.IconActive : UiColors.IconInactive);
    }

    /// <summary>
    /// Modal-style popup for adding a stream. Opened by the title-bar "+"
    /// (via <see cref="openAddStreamRequested"/>). Auto-closes on Play
    /// success, Cancel, click-outside, or Esc — all standard ImGui popup
    /// behavior. The popup re-uses the persistent <see cref="urlInput"/>
    /// buffer so a half-typed URL survives an accidental dismissal.
    /// </summary>
    private void DrawAddStreamPopup()
    {
        // 420px is wide enough for a typical YouTube URL plus the trailing
        // playlist params without horizontal scroll on the input field.
        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##addStream")) return;

        ImGui.TextUnformatted("Add stream");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped("Paste a Twitch / YouTube / Icecast / Shoutcast / MP3 stream URL.");
        UiHelpers.HelpMarker(
            "Need a URL?\n" +
            "  • Listener: ask a DJ for their Twitch / YouTube channel or stream URL\n" +
            "  • DJ: see Help → \"I want to DJ\"\n" +
            "  • Just testing: try https://www.youtube.com/watch?v=9Tzc3ybp8vA\n" +
            "    or https://ice1.somafm.com/groovesalad-128-mp3");
        ImGui.Spacing();

        // Auto-focus the input on first frame of the popup so the user can
        // immediately paste without an extra click. SetKeyboardFocusHere
        // applies to the next item; without it the popup would steal focus
        // but leave no widget claimed.
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        var enterPressed = ImGui.InputText("##url", ref urlInput, 1024,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.Spacing();
        var playClicked = ImGui.Button("Play", new Vector2(80, 0));
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        if (playClicked || enterPressed)
        {
            try
            {
                plugin.Config.LastStreamUrl = urlInput;
                plugin.Config.Save();
                plugin.PlayStream(urlInput);
                ImGui.CloseCurrentPopup();
                // Now Playing header + chat ("Playing: ...") already cover the
                // visual feedback for a successful start.
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Play failed from MusicPlayerWindow");
                plugin.Notify("ClubFFXIV: play failed",
                    ex.Message, NotificationType.Error, durationSeconds: 8);
                // Leave the popup open on error so the user can adjust the
                // URL without retyping.
            }
        }

        ImGui.EndPopup();
    }

    private void DrawProximityStatusSection()
    {
        ImGui.TextUnformatted($"Playback: {plugin.CurrentMode}");

        var prox = plugin.CurrentProximity;
        if (prox.HasValue)
        {
            var p = prox.Value;
            var label = p.Streaming
                ? (p.Audible ? "Approaching" : "Pre-buffering")
                : "Closest (out of range)";
            ImGui.Text($"{label}: {p.Candidate.DisplayName}");
            ImGui.Text($"Distance: {p.Distance:F1} yalms");

            var balance = p.Balance;
            var balancePos = Math.Clamp((int)Math.Round((balance + 1f) * 5f), 0, 10);
            var balanceBar = new string('-', balancePos) + 'o' + new string('-', 10 - balancePos);
            var balanceSide = balance < -0.05f ? $"{Math.Abs(balance) * 100f:F0}% left"
                            : balance >  0.05f ? $"{balance * 100f:F0}% right"
                            : "centered";
            ImGui.Text($"L [{balanceBar}] R  ({balanceSide})");

            // F/B mirrors the L/R block: fade is +1 in front, -1 behind, 0
            // perpendicular. Bar reads back→front so "F" labels the right
            // edge, matching how the player faces forward.
            var fade = p.Fade;
            var fadePos = Math.Clamp((int)Math.Round((fade + 1f) * 5f), 0, 10);
            var fadeBar = new string('-', fadePos) + 'o' + new string('-', 10 - fadePos);
            var fadeSide = fade >  0.05f ? $"{fade * 100f:F0}% front"
                         : fade < -0.05f ? $"{Math.Abs(fade) * 100f:F0}% back"
                         : "perpendicular";
            ImGui.Text($"B [{fadeBar}] F  ({fadeSide})");
        }
        else if (plugin.CurrentWard.HasValue)
        {
            ImGui.TextDisabled("No clubs found in this ward.");
        }
    }
}
