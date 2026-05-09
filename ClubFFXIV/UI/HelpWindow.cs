using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

public sealed class HelpWindow : Window, IDisposable
{
    private const string GuideUrl =
        "https://github.com/electr0sheep/ClubFFXIV/blob/main/docs/DJ-BROADCASTING.md";

    public HelpWindow()
        : base("ClubFFXIV — Getting Started##ClubFFXIVHelp", ImGuiWindowFlags.NoCollapse)
    {
        Size = new Vector2(560, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##helpTabs"))
        {
            if (ImGui.BeginTabItem("I'm a Listener"))
            {
                DrawListener();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("I want to DJ"))
            {
                DrawDj();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("FAQ"))
            {
                DrawFaq();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawListener()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "ClubFFXIV plays internet radio streams while you're in housing — " +
            "auto-tunes when you enter a club, fades in muffled when you approach " +
            "from outside.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Supported URL types");
        ImGui.BulletText("Twitch channel — twitch.tv/YourFavoriteDJ");
        ImGui.BulletText("YouTube Live / video — youtube.com/watch?v=...");
        ImGui.BulletText("Icecast / Shoutcast / direct MP3 stream");
        ImGui.BulletText("SoundCloud track or stream");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "(First Twitch/YouTube use auto-downloads ~80 MB of helper binaries " +
            "— ffmpeg + yt-dlp. One-time, then it's instant.)");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Got a stream URL from a friend?");
        ImGui.BulletText("Paste it into the \"Stream URL\" field in /pclub config");
        ImGui.BulletText("Click Play");
        ImGui.Spacing();

        SectionHeader("Want auto-discovery in housing?");
        ImGui.BulletText("Set a Registry URL in the Registry tab");
        ImGui.BulletText("Walk into a registered house — music auto-plays");
        ImGui.BulletText("Walk near a registered plot outdoors — muffled music swells as you approach");
        ImGui.Spacing();

        SectionHeader("Try it now — known-good streams:");
        CopyableUrl("Square Enix Music Channel - Chill (YouTube Live)", "https://www.youtube.com/watch?v=9Tzc3ybp8vA");
        CopyableUrl("SomaFM Groove Salad",   "https://ice1.somafm.com/groovesalad-128-mp3");
        CopyableUrl("SomaFM Drone Zone",     "https://ice1.somafm.com/dronezone-128-mp3");
        CopyableUrl("SomaFM Indie Pop Rocks", "https://ice1.somafm.com/indiepop-128-mp3");
        ImGui.Spacing();
        ImGui.TextDisabled("(Click 'Copy' next to a URL, then paste into the Stream URL field.)");
        ImGui.TextDisabled("(YouTube Live triggers a one-time ~80MB download of ffmpeg + yt-dlp.)");
    }

    private void DrawDj()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Hosting a club means broadcasting an audio stream that listeners tune into. " +
            "Most FFXIV venues already broadcast on Twitch — if you do too, just paste " +
            "your channel URL and you're done. The Icecast / managed-hosting paths below " +
            "are alternatives if you want full control.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Easiest path: stream on Twitch (or YouTube Live)");
        ImGui.TextWrapped(
            "If you already broadcast your DJ sets on Twitch (the de facto FFXIV venue " +
            "platform) or YouTube Live, listeners just paste your channel URL into ClubFFXIV. " +
            "Zero new infrastructure on your side.");
        ImGui.Spacing();
        ImGui.BulletText("Stream as you normally do — OBS, Streamlabs, whatever");
        ImGui.BulletText("Share your channel URL: e.g. twitch.tv/yourchannel");
        ImGui.BulletText("Listeners paste it into Stream URL → Play (or your house auto-tunes if published)");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "Latency is ~5–10 seconds (Twitch HLS), so all listeners are roughly synced " +
            "with each other but ~10 s behind your live decks.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Alternative: run your own Icecast (full control)");
        ImGui.TextWrapped(
            "If you want lower latency or don't want to use Twitch/YouTube, " +
            "you can run your own Icecast server. More setup work but no platform dependency.");
        ImGui.Spacing();

        SectionHeader("You need three things:");
        ImGui.BulletText("Source software — DJ deck running on your PC");
        ImGui.BulletText("Stream server — relays your audio to many listeners");
        ImGui.BulletText("Public URL — what you paste into ClubFFXIV");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Free path (recommended for friends-only)");
        ImGui.TextWrapped(
            "Run Icecast on your own PC, expose it publicly via Cloudflare Tunnel. " +
            "No paid hosting, no domain needed for testing.");
        ImGui.Spacing();
        ImGui.BulletText("Install Docker Desktop:");
        ImGui.SameLine();
        Hyperlink("docker.com", "https://www.docker.com/products/docker-desktop/");
        ImGui.BulletText("Install cloudflared:");
        ImGui.SameLine();
        Hyperlink("cloudflare docs", "https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/");
        ImGui.BulletText("Run Icecast in Docker, then `cloudflared tunnel --url http://localhost:8000`");
        ImGui.BulletText("cloudflared prints a free `*.trycloudflare.com` URL — paste it into ClubFFXIV");
        ImGui.BulletText("For a stable URL, own a domain on Cloudflare DNS — see the full guide");
        ImGui.BulletText("Bandwidth ceiling = your home upload speed (~50 listeners on 25 Mbps)");
        ImGui.BulletText("Caveat: PC sleeping / shut down = stream offline");
        ImGui.Spacing();
        ImGui.Text("Other free option:");
        ImGui.SameLine();
        Hyperlink("Oracle Cloud Free Tier", "https://www.oracle.com/cloud/free/");
        ImGui.SameLine();
        ImGui.TextDisabled("(always-on, dedicated server, but signup requires CC)");
        ImGui.Spacing();

        SectionHeader("Easiest path (managed, paid)");
        ImGui.TextWrapped("Use Mixxx as your source, a managed cloud service for hosting. ~$15-25/month, no setup work.");
        ImGui.Spacing();
        ImGui.BulletText("Install Mixxx — free DJ software:");
        ImGui.SameLine();
        Hyperlink("mixxx.org", "https://mixxx.org/download/");
        ImGui.BulletText("Sign up for hosting:");
        ImGui.Indent();
        Hyperlink("Azuracast Cloud", "https://www.azuracast.com/cloud/");
        ImGui.SameLine();
        ImGui.TextDisabled("• ~$15/mo, recommended");
        Hyperlink("Radio.co", "https://radio.co");
        ImGui.SameLine();
        ImGui.TextDisabled("• ~$21/mo");
        ImGui.Unindent();
        ImGui.BulletText("Use the stream URL the host gives you");
        ImGui.Spacing();

        SectionHeader("Cheapest reliable path ($5/mo VPS)");
        ImGui.TextWrapped("$5 VPS + Docker Icecast + Mixxx. More setup work but always-on with no free-tier risk.");
        ImGui.Spacing();
        ImGui.BulletText("Read the step-by-step guide:");
        ImGui.SameLine();
        Hyperlink("DJ-BROADCASTING.md", GuideUrl);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Once you have a stream URL");
        ImGui.BulletText("Stand inside your house");
        ImGui.BulletText("Open /pclub config → paste URL into Stream URL field");
        ImGui.BulletText("Click \"Save URL for this house (local)\" to auto-play it for yourself");
        ImGui.BulletText("Click \"Publish to registry\" to let other plugin users discover it");
        ImGui.BulletText("Walk outside to your front door, click \"Calibrate door\"");
        ImGui.BulletText("Friends within ~40m of your door will hear muffled music as they approach");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("A note on music licensing");
        ImGui.TextWrapped(
            "Streaming copyrighted music is technically a broadcast and requires licensing. " +
            "Small communities largely operate in a gray area, but DMCA risk exists for popular streams. " +
            "Safest options: royalty-free music (Free Music Archive, Pixabay), Creative Commons, " +
            "or your own original mixes. The plugin author isn't responsible for what DJs choose to stream.");
        ImGui.Spacing();

        if (ImGui.Button("Open full broadcasting guide"))
            OpenUrl(GuideUrl);
    }

    private void DrawFaq()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Quick answers to the most common questions. Still stuck? " +
            "The Listener and DJ tabs go deeper.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Does this work with Spotify or Apple Music?");
        ImGui.TextWrapped(
            "No. Both services use DRM-encrypted streams that only their official apps can play, " +
            "and there's no legal way around that. If your playlist also exists on YouTube or " +
            "YouTube Music, paste that URL instead — free Spotify-to-YouTube converters can " +
            "rebuild a playlist in a couple of clicks.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("I want my YouTube Music playlist to play in my house. How?");
        ImGui.BulletText("Open /pclub config → Now Playing, paste the playlist URL, click Play to test");
        ImGui.BulletText("Settings tab → enable \"Random playlist order\" if you want shuffle");
        ImGui.BulletText("Settings tab → enable \"Loop videos when they finish\" so it never runs out");
        ImGui.BulletText("My Clubs tab → \"Create local override\" to bind it to this house — auto-plays when you walk in");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("YouTube says \"Sign in to confirm you're not a bot.\" Now what?");
        ImGui.TextWrapped(
            "Open the Advanced tab and set \"Cookies from browser\" to firefox. You must already " +
            "be logged into YouTube in Firefox — yt-dlp reads your session cookies from there. " +
            "Chromium-based browsers (Chrome, Edge, Brave, Opera, Vivaldi) encrypt cookies in a " +
            "way yt-dlp can't read without extra setup, so Firefox is the easy answer.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Can I play a local MP3 from my computer?");
        ImGui.TextWrapped(
            "Not directly — the plugin only plays URLs. If you want your own files to be the " +
            "soundtrack for friends visiting, the realistic path is to host them on your own " +
            "Icecast server (free with Cloudflare Tunnel — see the I want to DJ tab).");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Music keeps playing when I alt-tab. How do I mute it?");
        ImGui.TextWrapped(
            "The plugin follows FFXIV's own \"Play sounds when window is not active\" toggles — " +
            "no separate setting on our end. In-game: System Configuration → Sound Settings, " +
            "uncheck both \"Play sound effects when window is not active\" and \"Play BGM when " +
            "window is not active.\" The plugin will mute the moment you alt-tab.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Why is the first stream so slow to start?");
        ImGui.TextWrapped(
            "First time you play a Twitch / YouTube / SoundCloud URL, the plugin downloads " +
            "~80 MB of helper binaries (ffmpeg + yt-dlp). One-time per install — every later " +
            "stream is instant. Direct Icecast / Shoutcast streams skip the download entirely.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("How do I share my stream with a friend?");
        ImGui.BulletText("Send them the URL — they paste into /pclub config and click Play");
        ImGui.BulletText("DJing from a house? \"Publish new club\" in My Clubs lets any plugin user discover it");
        ImGui.BulletText("Want it private? Set a passphrase on publish — listeners enter it once, then it's cached");
    }

    private static void SectionHeader(string text)
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), text);
    }

    private static void Hyperlink(string label, string url)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.7f, 1f, 1f));
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked()) OpenUrl(url);
        }
    }

    private static void CopyableUrl(string label, string url)
    {
        ImGui.BulletText($"{label}:");
        ImGui.SameLine();
        ImGui.TextDisabled(url.Length > 50 ? url[..47] + "..." : url);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Copy##{label}"))
        {
            ImGui.SetClipboardText(url);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not open URL: {ex.Message}");
            ImGui.SetClipboardText(url);
        }
    }
}
