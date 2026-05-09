using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace ClubFFXIV.UI;

/// <summary>
/// Caches remote thumbnail URLs as ImGui-renderable textures for the Now
/// Playing header. Dalamud SDK 15's <c>ITextureProvider</c> has no
/// <c>GetFromUrl</c> helper — only file/game/manifest sources — so we fetch
/// the image bytes ourselves with a shared <c>HttpClient</c> and feed them
/// to <c>CreateFromImageAsync</c>. Each URL is fetched at most once per
/// session; results stay resident until <see cref="DisposeAll"/> is called
/// from <c>Plugin.Dispose</c> (the wraps are GPU-resident textures we own
/// and must release explicitly).
///
/// Sources without artwork (Icecast, direct-HTTP MP3, yt-dlp before its
/// first --print line lands) pass <c>thumbnailUrl=null</c> and get a
/// neutral placeholder square the same size as a loaded thumbnail, so the
/// row layout doesn't shift when the real image arrives.
/// </summary>
internal static class NowPlayingThumbnails
{
    private enum State { Loading, Loaded, Failed }

    private sealed class Entry
    {
        public State State;
        public IDalamudTextureWrap? Wrap;
    }

    private static readonly ConcurrentDictionary<string, Entry> cache = new();
    // Single shared HttpClient — reuse keeps the socket pool warm and
    // avoids the well-known "thousands of TIME_WAIT" anti-pattern of
    // per-request clients.
    private static readonly HttpClient http = CreateHttpClient();
    // Cap concurrent fetches so a high-voice multi-stream session doesn't
    // spawn a TLS handshake per voice all at once. 4 is comfortable for
    // thumbnails (typically <100 KB each).
    private static readonly SemaphoreSlim fetchGate = new(4, 4);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ClubFFXIV/0.1");
        return c;
    }

    /// <summary>
    /// Draw a thumbnail at the current cursor position, advancing the cursor
    /// by exactly <paramref name="size"/> in either branch (loaded image or
    /// placeholder). When <paramref name="cropToSquare"/> is true, the loaded
    /// image is cover-cropped to <paramref name="size"/>'s aspect — used for
    /// YouTube Music / Topic-channel sources whose thumbnails are square
    /// album art padded into a 16:9 frame by YouTube's CDN. The caller is
    /// responsible for the surrounding SameLine / row layout.
    /// </summary>
    public static void Draw(string? thumbnailUrl, bool cropToSquare, Vector2 size)
    {
        if (!string.IsNullOrEmpty(thumbnailUrl) && TryDrawRemote(thumbnailUrl!, cropToSquare, size))
            return;

        DrawPlaceholder(size);
    }

    /// <summary>
    /// Release every cached texture wrap. Called from Plugin.Dispose so the
    /// GPU memory backing each thumbnail is freed on plugin reload.
    /// </summary>
    public static void DisposeAll()
    {
        foreach (var entry in cache.Values)
        {
            try { entry.Wrap?.Dispose(); }
            catch { /* never let cleanup throw on shutdown */ }
            entry.Wrap = null;
        }
        cache.Clear();
    }

    private static bool TryDrawRemote(string url, bool cropToSquare, Vector2 size)
    {
        var entry = cache.GetOrAdd(url, BeginFetch);
        if (entry.State == State.Loaded
            && entry.Wrap is { } wrap
            && wrap.Handle != IntPtr.Zero)
        {
            if (cropToSquare && wrap.Width > 0 && wrap.Height > 0)
            {
                var (uv0, uv1) = ComputeCoverCropUv(wrap.Width, wrap.Height, size);
                ImGui.Image(wrap.Handle, size, uv0, uv1);
            }
            else
            {
                ImGui.Image(wrap.Handle, size);
            }
            // Hovering the row thumbnail surfaces a larger preview in a
            // tooltip. Reuses the same wrap — already on the GPU, so it's
            // just a scaled draw, no extra fetch.
            if (ImGui.IsItemHovered())
                DrawHoverPreview(wrap, cropToSquare);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Render the loaded thumbnail at preview size inside an ImGui tooltip.
    /// For cover-cropped sources (YouTube Music / Topic) the preview shows
    /// the same cropped square the row shows, just larger — what the user
    /// hovered is what they get a closer look at. For uncropped sources the
    /// preview honours the source's native aspect ratio so the image isn't
    /// stretched into a fixed shape.
    /// </summary>
    private static void DrawHoverPreview(IDalamudTextureWrap wrap, bool cropToSquare)
    {
        if (wrap.Width <= 0 || wrap.Height <= 0) return;
        // 360 px on the longest edge keeps the popup useful on a 1080p
        // window without dominating the screen on smaller resolutions.
        const float MaxEdge = 360f;

        Vector2 displaySize;
        Vector2 uv0 = Vector2.Zero, uv1 = Vector2.One;

        if (cropToSquare)
        {
            displaySize = new Vector2(MaxEdge, MaxEdge);
            (uv0, uv1) = ComputeCoverCropUv(wrap.Width, wrap.Height, displaySize);
        }
        else
        {
            // Scale-to-fit: longest edge clamped to MaxEdge, other axis follows.
            var aspect = (float)wrap.Width / wrap.Height;
            displaySize = aspect >= 1f
                ? new Vector2(MaxEdge, MaxEdge / aspect)
                : new Vector2(MaxEdge * aspect, MaxEdge);
        }

        ImGui.BeginTooltip();
        ImGui.Image(wrap.Handle, displaySize, uv0, uv1);
        ImGui.EndTooltip();
    }

    /// <summary>
    /// Cover-crop UVs that fit a <paramref name="imgW"/>×<paramref name="imgH"/>
    /// texture into a <paramref name="slot"/>-shaped rectangle: trim the wider
    /// axis equally on both sides so the centre region matches the slot's
    /// aspect. For a 913×514 source in a square slot, this is exactly the
    /// central 514×514 region — i.e. YouTube's container bars get cropped out.
    /// </summary>
    private static (Vector2 uv0, Vector2 uv1) ComputeCoverCropUv(int imgW, int imgH, Vector2 slot)
    {
        var imgAspect = (float)imgW / imgH;
        var slotAspect = slot.X / slot.Y;
        // Already-matching aspects are a no-op — skip the float math and
        // return identity UVs to avoid any 0.5+epsilon rounding drift.
        if (Math.Abs(imgAspect - slotAspect) < 0.001f)
            return (Vector2.Zero, Vector2.One);

        if (imgAspect > slotAspect)
        {
            // Image wider than slot → crop horizontally (sides). visibleU is
            // the fraction of source width we keep; pad is the trim each side.
            var visibleU = slotAspect / imgAspect;
            var pad = (1f - visibleU) * 0.5f;
            return (new Vector2(pad, 0f), new Vector2(1f - pad, 1f));
        }
        else
        {
            // Image taller than slot → crop vertically (top/bottom).
            var visibleV = imgAspect / slotAspect;
            var pad = (1f - visibleV) * 0.5f;
            return (new Vector2(0f, pad), new Vector2(1f, 1f - pad));
        }
    }

    private static Entry BeginFetch(string url)
    {
        var entry = new Entry { State = State.Loading };
        // Fire-and-forget — the entry is stored in the cache *before* the
        // task runs, so concurrent calls for the same URL all see the same
        // Loading entry and only one fetch is initiated. We never await
        // this task; the next ImGui frame after Loaded transitions will
        // pick up the wrap.
        _ = Task.Run(() => FetchAsync(url, entry));
        return entry;
    }

    private static async Task FetchAsync(string url, Entry entry)
    {
        await fetchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var wrap = await Plugin.TextureProvider
                .CreateFromImageAsync(bytes)
                .ConfigureAwait(false);
            entry.Wrap = wrap;
            entry.State = State.Loaded;
        }
        catch
        {
            // Network failures, 404s, decode errors — once Failed we don't
            // retry for the rest of the session. A reload of the plugin
            // (or reopening the same URL) re-attempts.
            entry.State = State.Failed;
        }
        finally
        {
            fetchGate.Release();
        }
    }

    private static void DrawPlaceholder(Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        // Muted square sitting behind a one-pixel border. Same hue family as
        // the dark Now Playing strip so it reads as "reserved space" rather
        // than a UI element competing with the title text.
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.20f, 1f)));
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.34f, 1f)));
        ImGui.Dummy(size);
    }
}
