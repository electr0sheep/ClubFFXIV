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

        // Gear button in the title bar → toggles the settings window. Living
        // here (not in ConfigWindow) keeps the music-player as the user's
        // single point of entry: open /pclub, click the gear when needed.
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
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawHelpBar();
        DrawNowPlayingHeader();
        ImGui.Spacing();
        DrawStreamSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawProximityStatusSection();
    }

    private void DrawHelpBar()
    {
        var firstRun = string.IsNullOrEmpty(plugin.Config.LastStreamUrl);

        if (firstRun)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.25f, 0.35f, 0.6f));
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
        else
        {
            // Subtle help button on the right; doesn't take a full row.
            var label = "? Help";
            var btnW = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + 4;
            ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X - btnW + ImGui.GetCursorPosX());
            if (ImGui.SmallButton(label))
                plugin.ToggleHelp();
        }
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

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.10f, 0.12f, 1f));
        ImGui.BeginChild("##nowPlayingHeader", new Vector2(-1, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        if (entries.Count == 0)
        {
            // Static placeholder — no playback to act on.
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), FontAwesomeIcon.Pause.ToIconString());
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
        var icon = muted ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;

        ImGui.AlignTextToFramePadding();
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), icon.ToIconString());
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(muted ? "Click to unmute" : "Click to mute");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            plugin.ToggleNowPlayingMute(entry);

        // Play/Pause icon for non-live rows. Sits right of the mute icon and
        // shows the action that will happen on click (Play when paused, Pause
        // when playing). Live rows omit this entirely — the existing mute
        // chrome is the only meaningful control there.
        if (!entry.IsLive)
        {
            ImGui.SameLine();
            var playIcon = entry.IsPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
            ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
            ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), playIcon.ToIconString());
            ImGui.PopFont();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(entry.IsPaused ? "Click to play" : "Click to pause");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                plugin.ToggleNowPlayingPause(entry);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(entry.Label);
        // Full URL on hover, in case the truncation hid the relevant tail.
        if (entry.Label != entry.Url && ImGui.IsItemHovered())
            ImGui.SetTooltip(entry.Url);

        // Right-aligned Blacklist button. Idiom matches the "? Help" pattern
        // — measure label width and offset the cursor.
        const string blacklistLabel = "Blacklist";
        var pad = ImGui.GetStyle().FramePadding.X * 2 + 4;
        var blacklistW = ImGui.CalcTextSize(blacklistLabel).X + pad;
        ImGui.SameLine();
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - blacklistW);
        if (ImGui.SmallButton(blacklistLabel + "##" + entry.Url))
            plugin.BlacklistNowPlaying(entry);

        // Second line: seek bar + timestamp for non-live entries. The
        // header's height calculation reserves space for this so it doesn't
        // overflow the bordered child window.
        if (HasTransportRow(entry))
            DrawNowPlayingTransport(entry, thumbSize);

        ImGui.PopID();
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

    private void DrawStreamSection()
    {
        ImGui.TextWrapped("Paste a Twitch / YouTube / Icecast / Shoutcast / MP3 stream URL.");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Need a URL?\n" +
                "  • Listener: ask a DJ for their Twitch / YouTube channel or stream URL\n" +
                "  • DJ: see Help → \"I want to DJ\"\n" +
                "  • Just testing: try https://www.youtube.com/watch?v=9Tzc3ybp8vA\n" +
                "    or https://ice1.somafm.com/groovesalad-128-mp3");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##url", ref urlInput, 1024);
        ImGui.Spacing();

        if (ImGui.Button("Play", new Vector2(80, 0)))
        {
            try
            {
                plugin.Config.LastStreamUrl = urlInput;
                plugin.Config.Save();
                plugin.PlayStream(urlInput);
                // Now Playing header + chat ("Playing: ...") already cover the
                // visual feedback for a successful start.
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Play failed from MusicPlayerWindow");
                plugin.Notify("ClubFFXIV: play failed",
                    ex.Message, NotificationType.Error, durationSeconds: 8);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop", new Vector2(80, 0)))
        {
            plugin.StopStream();
            // Now Playing header reflects "stopped" — no extra status needed.
        }

        ImGui.Spacing();
        var volume = plugin.Config.Volume;
        if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, "%.2f"))
        {
            plugin.Config.Volume = volume;
            plugin.Config.Save();
            plugin.SetStreamVolume(volume);
        }

        ImGui.Spacing();
        var keepInSub = plugin.Config.KeepPlayingInLinkedSubterritories;
        if (ImGui.Checkbox("Keep playing in FC workshop / linked sub-rooms", ref keepInSub))
        {
            plugin.Config.KeepPlayingInLinkedSubterritories = keepInSub;
            plugin.Config.Save();
        }
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
            ImGui.Text($"Distance: {p.Distance:F1} m   Nearness: {p.NormalizedNearness * 100:F0}%");
        }
        else if (plugin.CurrentWard.HasValue)
        {
            ImGui.TextDisabled("No clubs found in this ward.");
        }
    }
}
