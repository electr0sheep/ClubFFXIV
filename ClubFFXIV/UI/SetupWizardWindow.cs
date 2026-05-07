using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// First-run wizard. Three sections:
///   1. Pre-approve common stream-source domains so the user isn't bombarded
///      with permission prompts later.
///   2. Explain why ClubFFXIV needs yt-dlp + ffmpeg and let the user explicitly
///      download them. We do NOT silently auto-download — the user clicks here
///      or in /pclub config.
///   3. Explain why auto-update is a good idea and let the user opt out.
/// </summary>
public sealed class SetupWizardWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private static readonly (string Domain, string Description)[] CommonDomains =
    {
        ("twitch.tv",          "Twitch live streams"),
        ("youtube.com",        "YouTube videos & live streams"),
        ("youtu.be",           "YouTube share URLs"),
        ("ice1.somafm.com",    "SomaFM internet radio"),
        ("ice2.somafm.com",    "SomaFM (alternate)"),
        ("ice3.somafm.com",    "SomaFM (alternate)"),
        ("ice4.somafm.com",    "SomaFM (alternate)"),
        ("soundcloud.com",     "SoundCloud tracks & streams"),
        ("api-v2.soundcloud.com", "SoundCloud API streams"),
    };

    private readonly Dictionary<string, bool> selections = new();

    // Wizard-local copies of toggleable settings — applied to Config only on
    // Done so closing the window mid-wizard doesn't half-persist state.
    private bool wizardAutoUpdate;

    // Binary install state. The download itself runs on a Task; the wizard
    // polls these fields each frame to render status.
    private string binaryStatus = "";
    private bool binaryInstallInFlight;

    public SetupWizardWindow(Plugin plugin)
        : base("ClubFFXIV — Setup##ClubFFXIVSetup",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.plugin = plugin;
        Size = new Vector2(580, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        foreach (var (d, _) in CommonDomains) selections[d] = true;
        wizardAutoUpdate = plugin.Config.AutoUpdateBinaries;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextWrapped(
            "Welcome to ClubFFXIV! A short walk-through to get you set up — " +
            "you can change everything below later in /pclub config.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawDomainsSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawBinariesSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawAutoUpdateSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawFooter();
    }

    private void DrawDomainsSection()
    {
        ImGui.TextUnformatted("1. Pre-approve common stream domains");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "For your safety, the plugin asks before playing streams from any " +
            "new domain. Check the well-known hosts you trust to skip the prompt:");
        ImGui.Spacing();

        foreach (var (domain, desc) in CommonDomains)
        {
            var on = selections[domain];
            if (ImGui.Checkbox($"##chk_{domain}", ref on))
                selections[domain] = on;
            ImGui.SameLine();
            ImGui.TextUnformatted(domain);
            ImGui.SameLine();
            ImGui.TextDisabled($"— {desc}");
        }
    }

    private void DrawBinariesSection()
    {
        ImGui.TextUnformatted("2. Install external tools (yt-dlp + ffmpeg)");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Twitch, YouTube, SoundCloud, Mixcloud, Twitcasting, and Niconico " +
            "ship their streams in segmented / encrypted formats the plugin " +
            "can't decode on its own. ClubFFXIV uses two open-source tools " +
            "to handle them:");
        ImGui.Spacing();
        ImGui.BulletText("yt-dlp (~3 MB) — extracts the actual stream URL from those sites");
        ImGui.BulletText("ffmpeg (~80 MB) — decodes that stream into raw audio");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Direct MP3 / Icecast / Shoutcast / OGG streams (e.g. SomaFM, your " +
            "own Icecast server) do NOT need these — those are decoded in-process. " +
            "Skip this step if you only plan to use direct streams.");
        ImGui.Spacing();

        DrawBinaryStatusLine("yt-dlp", plugin.Binaries.YtDlpInstalled);
        DrawBinaryStatusLine("ffmpeg", plugin.Binaries.FfmpegInstalled);
        ImGui.Spacing();

        var ready = plugin.Binaries.Ready;
        var disabled = ready || binaryInstallInFlight;
        if (disabled) ImGui.BeginDisabled();
        var btnLabel = ready
            ? "Already installed"
            : binaryInstallInFlight
                ? "Downloading..."
                : "Download yt-dlp + ffmpeg (~83 MB)";
        if (ImGui.Button(btnLabel, new Vector2(280, 0)))
        {
            StartBinaryDownload();
        }
        if (disabled) ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(binaryStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(binaryStatus);
        }

        ImGui.Spacing();
        ImGui.TextDisabled(
            "You can also install (or check / update) later in /pclub config → " +
            "External binaries.");
    }

    private static void DrawBinaryStatusLine(string name, bool installed)
    {
        var color = installed
            ? new Vector4(0.4f, 0.85f, 0.4f, 1f)
            : new Vector4(0.85f, 0.5f, 0.4f, 1f);
        var prefix = installed ? "✓" : "✗";
        var suffix = installed ? "installed" : "not installed";
        ImGui.TextColored(color, $"  {prefix} {name} {suffix}");
    }

    private void StartBinaryDownload()
    {
        binaryInstallInFlight = true;
        binaryStatus = "Starting download...";
        _ = Task.Run(async () =>
        {
            try
            {
                await plugin.Binaries.EnsureInstalledAsync(progress: msg => binaryStatus = msg);
                binaryStatus = "Download complete.";
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Setup wizard: binary download failed");
                binaryStatus = $"Download failed: {ex.Message}";
            }
            finally
            {
                binaryInstallInFlight = false;
            }
        });
    }

    private void DrawAutoUpdateSection()
    {
        ImGui.TextUnformatted("3. Keep yt-dlp up to date");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Twitch and YouTube frequently change how their streams are " +
            "protected to deter scrapers. The yt-dlp project usually ships " +
            "fixes within a day — but without auto-updates, your Twitch / " +
            "YouTube playback will eventually break until you remember to " +
            "manually update.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "We strongly recommend leaving this on. yt-dlp self-updates via " +
            "'yt-dlp -U' (no system-level changes, no admin prompt), and the " +
            "check runs in the background at most once every 2 days.");
        ImGui.Spacing();

        ImGui.Checkbox(
            "Check for yt-dlp updates every 2 days  (recommended)",
            ref wizardAutoUpdate);

        if (!wizardAutoUpdate)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.95f, 0.7f, 0.2f, 1f),
                "  ⚠ With auto-update off, expect Twitch / YouTube playback to " +
                "break periodically.");
            ImGui.TextDisabled(
                "  Manually update from /pclub config → External binaries → Check / update.");
        }
    }

    private void DrawFooter()
    {
        if (binaryInstallInFlight) ImGui.BeginDisabled();
        if (ImGui.Button("Done", new Vector2(120, 0)))
        {
            foreach (var (domain, on) in selections)
                if (on) plugin.Config.AllowedDomains.Add(domain);
            plugin.Config.AutoUpdateBinaries = wizardAutoUpdate;
            plugin.Config.SetupWizardComplete = true;
            plugin.Config.Save();
            IsOpen = false;
        }
        if (binaryInstallInFlight) ImGui.EndDisabled();

        if (binaryInstallInFlight)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(wait for download to finish)");
        }
    }
}
