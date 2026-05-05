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

        SectionHeader("Got a stream URL from a friend?");
        ImGui.BulletText("Paste it into the \"Stream URL\" field in /club config");
        ImGui.BulletText("Click Play");
        ImGui.Spacing();

        SectionHeader("Want auto-discovery in housing?");
        ImGui.BulletText("Set a Registry URL in the Registry section");
        ImGui.BulletText("Walk into a registered house — music auto-plays");
        ImGui.BulletText("Walk near a registered plot outdoors — muffled music swells as you approach");
        ImGui.Spacing();

        SectionHeader("Try it now — known-good streams:");
        CopyableUrl("SomaFM Groove Salad",   "https://ice1.somafm.com/groovesalad-128-mp3");
        CopyableUrl("SomaFM Drone Zone",     "https://ice1.somafm.com/dronezone-128-mp3");
        CopyableUrl("SomaFM Indie Pop Rocks", "https://ice1.somafm.com/indiepop-128-mp3");
        ImGui.Spacing();
        ImGui.TextDisabled("(Click 'Copy' next to a URL, then paste into the Stream URL field.)");
    }

    private void DrawDj()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Hosting a club means broadcasting an audio stream that listeners tune into. " +
            "You don't stream from inside the game — you run a separate \"radio station\" " +
            "and tell ClubFFXIV its public URL.");
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
            "Costs nothing, no signups beyond a domain on Cloudflare (which you may " +
            "already have for the ClubFFXIV registry).");
        ImGui.Spacing();
        ImGui.BulletText("Install Docker Desktop:");
        ImGui.SameLine();
        Hyperlink("docker.com", "https://www.docker.com/products/docker-desktop/");
        ImGui.BulletText("Install cloudflared:");
        ImGui.SameLine();
        Hyperlink("cloudflare docs", "https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/");
        ImGui.BulletText("Run Icecast in Docker, route a tunnel to it");
        ImGui.BulletText("Bandwidth ceiling = your home upload speed (~50 listeners on 25 Mbps)");
        ImGui.BulletText("Caveat: PC sleeping / shut down = stream offline");
        ImGui.Spacing();
        ImGui.Text("Other free option:");
        ImGui.SameLine();
        Hyperlink("Oracle Cloud Free Tier", "https://www.oracle.com/cloud/free/");
        ImGui.SameLine();
        ImGui.TextDisabled("(always-on but signup requires CC)");
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
        ImGui.BulletText("Open /club config → paste URL into Stream URL field");
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
            Plugin.Log.Warning($"[ClubFFXIV] Could not open URL: {ex.Message}");
            ImGui.SetClipboardText(url);
        }
    }
}
