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
    /// placeholder). The caller is responsible for the surrounding SameLine /
    /// row layout.
    /// </summary>
    public static void Draw(string? thumbnailUrl, Vector2 size)
    {
        if (!string.IsNullOrEmpty(thumbnailUrl) && TryDrawRemote(thumbnailUrl!, size))
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

    private static bool TryDrawRemote(string url, Vector2 size)
    {
        var entry = cache.GetOrAdd(url, BeginFetch);
        if (entry.State == State.Loaded
            && entry.Wrap is { } wrap
            && wrap.ImGuiHandle != IntPtr.Zero)
        {
            ImGui.Image(wrap.ImGuiHandle, size);
            return true;
        }
        return false;
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
