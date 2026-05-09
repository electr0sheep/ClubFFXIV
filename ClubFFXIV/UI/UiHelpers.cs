using System;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// Shared widgets so every window draws section headers, inline help
/// markers, and status indicators the same way. Replaces an earlier mix
/// of bare <c>TextUnformatted</c> headers, <c>TextDisabled("(?)")</c>
/// help glyphs, and inline <c>TextDisabled("● connected")</c> dots.
/// </summary>
internal static class UiHelpers
{
    /// <summary>
    /// Coloured section heading. Pair with <see cref="ImGui.Separator"/>
    /// — pass <paramref name="separator"/>=true (the default) to draw the
    /// underline that visually anchors the heading to its content.
    /// </summary>
    public static void SectionHeader(string text, bool separator = true)
    {
        ImGui.TextColored(UiColors.SectionHeader, text);
        if (separator) ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Inline help marker — a <see cref="FontAwesomeIcon.QuestionCircle"/>
    /// glyph that surfaces <paramref name="tooltip"/> on hover. Replaces the
    /// previous <c>TextDisabled("(?)")</c> + manual hover wiring; identical
    /// behaviour, one call site, and the glyph reads as interactive.
    ///
    /// Call right after the widget you want to annotate; this places itself
    /// on the same line via <see cref="ImGui.SameLine()"/>.
    /// </summary>
    public static void HelpMarker(string tooltip)
    {
        ImGui.SameLine();
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        ImGui.TextColored(UiColors.IconInactive,
            FontAwesomeIcon.QuestionCircle.ToIconString());
        ImGui.PopFont();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    /// <summary>
    /// The four states a connection / install indicator can be in. Drives
    /// both the dot glyph (● solid for affirmative, ○ hollow otherwise) and
    /// the colour (green / red / muted grey). See <see cref="StatusDot"/>.
    /// </summary>
    public enum Status { Ok, Bad, Pending, Disabled }

    /// <summary>
    /// Coloured status dot followed by a label — e.g. "● connected" in green
    /// or "○ not connected" in red. Sits inline; caller is responsible for
    /// any <see cref="ImGui.SameLine"/> placement before/after.
    /// </summary>
    public static void StatusDot(Status status, string label)
    {
        var (color, glyph) = status switch
        {
            Status.Ok       => (UiColors.Success, "●"),
            Status.Bad      => (UiColors.Error,   "○"),
            Status.Pending  => (UiColors.Pending, "○"),
            Status.Disabled => (UiColors.Pending, "○"),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        ImGui.TextColored(color, $"{glyph} {label}");
    }
}
