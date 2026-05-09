using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// Renders thumbnail images for the Now Playing header. Wraps Dalamud's
/// <c>ITextureProvider.GetFromUrl</c> — that helper already de-duplicates by
/// URL and lazy-loads on first access, so this layer only adds the
/// "placeholder while loading / when missing" policy and a consistent draw
/// surface for the row renderer.
///
/// Sources without artwork (Icecast, direct-HTTP MP3, yt-dlp before its first
/// --print line lands) pass <c>thumbnailUrl=null</c> and get a neutral square
/// the same size as a loaded thumbnail, so the row layout doesn't shift
/// when the real image arrives a few frames later.
/// </summary>
internal static class NowPlayingThumbnails
{
    /// <summary>
    /// Draw a thumbnail at the current cursor position, advancing the cursor
    /// by exactly <paramref name="size"/> in either branch. The caller is
    /// responsible for the surrounding SameLine / row layout.
    /// </summary>
    public static void Draw(string? thumbnailUrl, Vector2 size)
    {
        if (!string.IsNullOrEmpty(thumbnailUrl) && TryDrawRemote(thumbnailUrl!, size))
            return;

        DrawPlaceholder(size);
    }

    private static bool TryDrawRemote(string url, Vector2 size)
    {
        try
        {
            // GetFromUrl is the documented entry point for arbitrary remote
            // images in Dalamud 10+. It returns a shared, URL-keyed texture
            // handle; calling it every frame is the intended usage —
            // duplicate calls share one underlying texture.
            var shared = Plugin.TextureProvider.GetFromUrl(url);
            if (shared.TryGetWrap(out var wrap, out _) && wrap is not null
                && wrap.ImGuiHandle != IntPtr.Zero)
            {
                ImGui.Image(wrap.ImGuiHandle, size);
                return true;
            }
        }
        catch
        {
            // GetFromUrl can throw on malformed URLs or fetcher init failures.
            // We never want a broken thumbnail to break the Now Playing UI —
            // fall through to the placeholder.
        }

        return false;
    }

    private static void DrawPlaceholder(Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        // Muted square that sits behind a one-pixel border. Same hue family
        // as the dark Now Playing strip so it reads as "reserved space" rather
        // than a UI element competing with the title text.
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.20f, 1f)));
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.34f, 1f)));
        ImGui.Dummy(size);
    }
}
