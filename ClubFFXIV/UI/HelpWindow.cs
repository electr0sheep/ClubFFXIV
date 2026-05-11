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
        Size = new Vector2(580, 680);
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
            "ClubFFXIV plays internet radio streams while you're in housing. " +
            "Paste any URL to play it manually, or let auto-discovery tune you " +
            "into nearby clubs as you wander a ward.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("How playback works");
        ImGui.BulletText("Manual play — title-bar  +  → paste URL → Play");
        ImGui.BulletText("Indoor auto — walk into a registered house, its stream auto-tunes");
        ImGui.BulletText("Outdoor auto — approach a calibrated plot, muffled audio swells with distance");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "The default registry is preconfigured, so auto-discovery just works. " +
            "No URL to enter — you only need to set a Registry URL in /pclub config → " +
            "Registry if you're running a private one.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Supported URL types");
        ImGui.BulletText("Twitch channel — twitch.tv/YourFavoriteDJ");
        ImGui.BulletText("YouTube — videos, live streams, and playlists");
        ImGui.BulletText("Icecast / Shoutcast / direct MP3 or OGG stream");
        ImGui.BulletText("SoundCloud, Mixcloud, Twitcasting, Niconico");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "First time you play a Twitch / YouTube / SoundCloud URL, the plugin " +
            "needs to install yt-dlp + ffmpeg + Deno (~123 MB). The Setup Wizard " +
            "prompts you to do this — nothing downloads silently. Direct Icecast / " +
            "MP3 streams skip the install entirely.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("The Now Playing player");
        ImGui.BulletText("Mute / unmute — click the speaker icon on a row");
        ImGui.BulletText("Pause / skip — non-live rows show play/pause and skip-next");
        ImGui.BulletText("Seek — drag the seek bar on a non-live row (YouTube videos, etc.)");
        ImGui.BulletText("Blacklist — the Ban icon blocks a URL from ever playing again");
        ImGui.BulletText("Loop / Random — toggle icons next to Stop, for playlists");
        ImGui.BulletText("Volume — single slider applies to every active stream");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Browse what's already published");
        ImGui.TextWrapped(
            "/pclub directory opens the Public Directory — every club whose DJ opted " +
            "into the browse list. Sort and filter by name, location, description, or " +
            "URL; click a URL to copy it to your clipboard.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("What you might see the first time");
        ImGui.BulletText("URL approval prompt — first time a host comes up (e.g., a new Icecast server),");
        ImGui.Indent();
        ImGui.TextDisabled("you choose Allow this URL, Skip, or Block. Allowing trusts only that exact URL;");
        ImGui.TextDisabled("\"Allow all from {host}\" is a separate, deliberately friction-y action.");
        ImGui.Unindent();
        ImGui.BulletText("Passphrase prompt — some clubs are password-protected. Ask the DJ for the");
        ImGui.Indent();
        ImGui.TextDisabled("passphrase, enter it once; the plugin caches it so you won't be prompted again.");
        ImGui.Unindent();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Try it now — known-good streams:");
        CopyableUrl("Square Enix Music Channel - Chill (YouTube Live)", "https://www.youtube.com/watch?v=9Tzc3ybp8vA");
        CopyableUrl("SomaFM Groove Salad",   "https://ice1.somafm.com/groovesalad-128-mp3");
        CopyableUrl("SomaFM Drone Zone",     "https://ice1.somafm.com/dronezone-128-mp3");
        CopyableUrl("SomaFM Indie Pop Rocks", "https://ice1.somafm.com/indiepop-128-mp3");
        ImGui.Spacing();
        ImGui.TextDisabled("(Click 'Copy' next to a URL, then paste into the Add Stream popup.)");
        ImGui.TextDisabled("(The SomaFM URLs are direct Icecast — no helper-binary install required.)");
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
        ImGui.BulletText("Listeners paste it into the Add Stream popup → Play (or your house auto-tunes if published)");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "Latency is ~5–10 seconds (Twitch HLS), so all listeners are roughly synced " +
            "with each other but ~10s behind your live decks.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Alternative: run your own Icecast (full control)");
        ImGui.TextWrapped(
            "If you want lower latency or don't want to depend on Twitch / YouTube, " +
            "you can run your own Icecast server. More setup, but no platform lock-in.");
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
        ImGui.TextWrapped("Use Mixxx as your source, a managed cloud service for hosting. ~$15–25/month, no setup work.");
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
        ImGui.TextWrapped(
            "Stand inside your house, then open /pclub config → My Clubs. The Current " +
            "Location section there has two distinct paths:");
        ImGui.Spacing();

        SectionHeader("Path 1 — Create local override (private, you-only)");
        ImGui.BulletText("Binds the URL to this plot for your client only");
        ImGui.BulletText("Auto-plays when you walk in; no one else sees or hears it");
        ImGui.BulletText("Useful for personal house playlists or for testing a URL before publishing");
        ImGui.Spacing();

        SectionHeader("Path 2 — Publish new club (shared, registry-backed)");
        ImGui.BulletText("Name, description, URL — listed in the Public Directory unless you uncheck Show in public directory");
        ImGui.BulletText("Optional: Password-protect this club — auto-generates a 6-word EFF diceware passphrase");
        ImGui.Indent();
        ImGui.TextDisabled("Share it with friends in Discord, etc. Listeners enter it once, the plugin caches it.");
        ImGui.TextDisabled("The registry only stores an Argon2id hash + salt — never the plaintext.");
        ImGui.Unindent();
        ImGui.BulletText("Then walk outside to your front door, find your house in the My Houses table, click the crosshair icon to calibrate");
        ImGui.BulletText("Friends within ~40 yalms of your door will hear muffled music as they approach");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "Note: Publish is disabled if the plugin detects you don't own the plot. " +
            "The check is local (FFXIVClientStructs) — it's a guardrail against accidental " +
            "publishing in a friend's house, not a serious anti-abuse measure.");
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
            "Quick answers to common questions. Still stuck? The Listener and DJ tabs go deeper.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("What's a local override?");
        ImGui.TextWrapped(
            "A URL bound to a plot for your client only. When you walk into that plot, " +
            "the override plays — even if the registry has a different URL for the same " +
            "plot, even if the plot isn't published at all. It's invisible to everyone " +
            "but you. Good for personal house playlists you don't want to share, or for " +
            "testing a stream before publishing it.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("What's the difference between a local override and an unlisted club?");
        ImGui.TextWrapped(
            "A local override is private to your client and never touches the registry. " +
            "An unlisted club (Show in public directory unchecked) is still published — " +
            "anyone whose ward you're in or who knows your plot key will still discover " +
            "it. Both have their place: local overrides for true privacy, unlisted clubs " +
            "for \"my friends know how to find this but it's not on the public browse list.\"");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Why is Publish disabled in this house?");
        ImGui.TextWrapped(
            "The plugin checks the game's local state to confirm you actually own the " +
            "plot before letting you publish. The check is best-effort (a forked client " +
            "could bypass it), so it's a guardrail against accidentally claiming a friend's " +
            "house — not a serious anti-abuse measure. If you do own the plot and the check " +
            "is wrong, the badge in Current Location will tell you what the plugin sees.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("What does password-protect actually do?");
        ImGui.TextWrapped(
            "When you publish a club with a passphrase, the plugin generates 6 words from " +
            "the EFF long diceware list (~155 bits of entropy), hashes the result with " +
            "Argon2id, and sends only the hash + salt to the registry. Listeners who know " +
            "the passphrase derive the same key, prove they have it, and the registry " +
            "returns the real stream URL. Anyone without the passphrase gets silence. The " +
            "plaintext never leaves your machine.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("The plugin asked me to approve a URL. What is this?");
        ImGui.TextWrapped(
            "URL permissioning: when a stream from an unfamiliar host appears (manual " +
            "paste, auto-discovery, etc.), the plugin asks before playing it. \"Allow this " +
            "URL\" trusts only that exact URL; \"Allow all URLs from {host}\" is broader and " +
            "warned about separately. Anyone can publish to the registry — club names and " +
            "descriptions aren't verified identity, so the prompt is what stops a malicious " +
            "DJ from auto-playing arbitrary URLs in your ear.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Does this work with Spotify or Apple Music?");
        ImGui.TextWrapped(
            "No. Both services use DRM-encrypted streams that only their official apps can " +
            "play, and there's no legal way around that. If your playlist also exists on " +
            "YouTube or YouTube Music, paste that URL instead — free Spotify-to-YouTube " +
            "converters can rebuild a playlist in a couple of clicks.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("I want my YouTube Music playlist to play in my house. How?");
        ImGui.BulletText("Open /pclub → title-bar +, paste the playlist URL, click Play to test");
        ImGui.BulletText("Music player toolbar → enable Random order (the dice icon) if you want shuffle");
        ImGui.BulletText("Music player toolbar → enable Loop (the repeat icon) so it never runs out");
        ImGui.BulletText("/pclub config → My Clubs → Create local override → bind the URL to this plot");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("YouTube says \"Sign in to confirm you're not a bot.\" Now what?");
        ImGui.TextWrapped(
            "Open /pclub config → Advanced → External binaries and set Cookies from " +
            "browser to firefox. You must already be logged into YouTube in Firefox — " +
            "yt-dlp reads your session cookies from there. Chromium-based browsers " +
            "(Chrome, Edge, Brave, Opera, Vivaldi) encrypt cookies in a way yt-dlp can't " +
            "read without extra setup, so Firefox is the easy answer.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Can I play a local MP3 from my computer?");
        ImGui.TextWrapped(
            "Not directly — the plugin only plays URLs. If you want your own files to be " +
            "the soundtrack for friends visiting, host them on your own Icecast server " +
            "(free with Cloudflare Tunnel — see the I want to DJ tab).");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Music keeps playing when I alt-tab. How do I mute it?");
        ImGui.TextWrapped(
            "The plugin follows FFXIV's own \"Play sounds when window is not active\" " +
            "toggles — no separate setting on our end. In-game: System Configuration → " +
            "Sound Settings, uncheck both \"Play sound effects when window is not active\" " +
            "and \"Play BGM when window is not active.\" The plugin will mute the moment " +
            "you alt-tab.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("Why is the first stream so slow to start?");
        ImGui.TextWrapped(
            "First time you play a Twitch / YouTube / SoundCloud URL, the plugin downloads " +
            "~123 MB of helper binaries (yt-dlp + ffmpeg + Deno). One-time per install — " +
            "every later stream is instant. Direct Icecast / Shoutcast streams skip the " +
            "download entirely. Deno is required because yt-dlp uses it to solve YouTube's " +
            "signature / n-challenge JavaScript.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionHeader("How do I share my stream with a friend?");
        ImGui.BulletText("Send them the URL — they paste it into /pclub → title-bar + and click Play");
        ImGui.BulletText("DJing from a house? Publish new club in My Clubs lets any plugin user discover it");
        ImGui.BulletText("Want it private? Set a passphrase on publish — listeners enter it once, then it's cached");
        ImGui.BulletText("Want it discoverable only by friends? Uncheck Show in public directory — still findable per-plot and by ward proximity, just not in the browse list");
    }

    // Help-window-local section header — distinct from UiHelpers.SectionHeader
    // because this one omits the underline (HelpWindow's content is densely
    // separated already, and the rule would compound). Same colour token,
    // so headings still read as "headings" against the rest of the UI.
    private static void SectionHeader(string text)
    {
        ImGui.TextColored(UiColors.SectionHeader, text);
    }

    private static void Hyperlink(string label, string url)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.Hyperlink);
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
