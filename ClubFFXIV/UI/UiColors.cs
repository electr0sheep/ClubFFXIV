using System.Numerics;

namespace ClubFFXIV.UI;

/// <summary>
/// Centralized colour tokens for the plugin UI. Every <see cref="Vector4"/>
/// literal that previously lived inline in window code now references a
/// named constant here, so a future theme change touches one file rather
/// than every <c>TextColored</c> call site. RGB triplets are kept in the
/// 0–1 float range ImGui expects.
/// </summary>
internal static class UiColors
{
    // --- Status / state ----------------------------------------------------

    /// <summary>Green — operations that succeeded, things that are on / installed.</summary>
    public static readonly Vector4 Success = new(0.40f, 0.85f, 0.40f, 1f);

    /// <summary>Amber — non-blocking warnings the user should notice but can ignore.</summary>
    public static readonly Vector4 Warning = new(0.95f, 0.70f, 0.20f, 1f);

    /// <summary>Red-orange — failures and dangerous actions.</summary>
    public static readonly Vector4 Error = new(0.85f, 0.50f, 0.40f, 1f);

    /// <summary>Muted grey — "in progress" / "checking..." indeterminate state.</summary>
    public static readonly Vector4 Pending = new(0.65f, 0.65f, 0.65f, 1f);

    // --- Typographic roles -------------------------------------------------

    /// <summary>Section headers — slight blue tint so headings stand apart from body text.</summary>
    public static readonly Vector4 SectionHeader = new(0.70f, 0.85f, 1.00f, 1f);

    /// <summary>Hyperlinks — the conventional clickable-blue.</summary>
    public static readonly Vector4 Hyperlink = new(0.45f, 0.70f, 1.00f, 1f);

    // --- Icon brightness ---------------------------------------------------

    /// <summary>Active / clickable icon glyphs (mute, play/pause, skip, stop).</summary>
    public static readonly Vector4 IconActive = new(0.85f, 0.85f, 0.85f, 1f);

    /// <summary>Toggle icons in their off state — readable but recedes.</summary>
    public static readonly Vector4 IconInactive = new(0.45f, 0.45f, 0.50f, 1f);

    /// <summary>Placeholder text inside the Now Playing strip ("Not playing").</summary>
    public static readonly Vector4 IconPlaceholder = new(0.60f, 0.60f, 0.60f, 1f);

    // --- Backgrounds / panels ---------------------------------------------

    /// <summary>The dark strip behind the Now Playing rows and URL/passphrase chips.</summary>
    public static readonly Vector4 PanelDark = new(0.10f, 0.10f, 0.12f, 1f);

    /// <summary>Tinted banner for the first-run nudge in the music player.</summary>
    public static readonly Vector4 BannerInfo = new(0.15f, 0.25f, 0.35f, 0.60f);

    /// <summary>Fill colour for a placeholder thumbnail (Icecast / direct-MP3
    /// streams that don't expose artwork). Same hue family as
    /// <see cref="PanelDark"/>, a touch lighter so the square reads as
    /// "reserved space" against the strip rather than disappearing into it.</summary>
    public static readonly Vector4 ThumbPlaceholderFill = new(0.18f, 0.18f, 0.20f, 1f);

    /// <summary>One-pixel border around a placeholder thumbnail.</summary>
    public static readonly Vector4 ThumbPlaceholderBorder = new(0.30f, 0.30f, 0.34f, 1f);
}
