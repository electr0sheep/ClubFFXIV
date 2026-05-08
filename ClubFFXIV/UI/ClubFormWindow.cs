using System;
using System.Numerics;
using System.Threading.Tasks;
using ClubFFXIV.Game;
using ClubFFXIV.Network;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// Single form window used by every club-edit entry point — Current Location's
/// "Create / Edit local override" and "Publish new / Edit club" buttons, plus
/// the My Houses table's row-level Edit actions. Each path opens with a mode
/// + plot key so the form knows what fields to show and which Plugin method
/// to call on submit. The window is reused across opens to avoid registering
/// a new Dalamud window per click.
/// </summary>
public sealed class ClubFormWindow : Window, IDisposable
{
    public enum FormMode
    {
        LocalCreate,
        LocalEdit,
        RegistryPublish,
        RegistryEdit,
    }

    private readonly Plugin plugin;

    private FormMode mode;
    private string targetPlotKey = "";
    private string headerLabel = "";
    private string nameInput = "";
    private string descInput = "";
    private string urlInput = "";
    private bool listedInput = true;
    // Registry-mode-only password gate. When the checkbox is on, the form
    // shows the passphrase plaintext (so the DJ can copy/share it) and the
    // generate button. The plaintext lives in passphraseInput; a blank
    // checkbox at submit time clears the password gate on the registry.
    private bool passwordProtectInput;
    private string passphraseInput = "";

    public ClubFormWindow(Plugin plugin)
        : base("Club##ClubFFXIVForm", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(540, 380);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
    }

    public void Dispose() { }

    public void OpenLocalCreate(PlotKey plot)
    {
        mode = FormMode.LocalCreate;
        targetPlotKey = plot.Canonical;
        headerLabel = plugin.HousingDetector.GetDisplayName(plot);
        nameInput = headerLabel;
        descInput = "";
        urlInput = "";
        listedInput = true;
        WindowName = "Create local override##ClubFFXIVForm";
        IsOpen = true;
    }

    public void OpenLocalEdit(string plotKey, ClubEntry entry)
    {
        mode = FormMode.LocalEdit;
        targetPlotKey = plotKey;
        headerLabel = LabelForKey(plotKey, entry.DisplayName);
        nameInput = entry.DisplayName;
        descInput = entry.Description;
        urlInput = entry.StreamUrl;
        listedInput = entry.Listed;
        WindowName = "Edit local override##ClubFFXIVForm";
        IsOpen = true;
    }

    public void OpenRegistryPublish(PlotKey plot)
    {
        mode = FormMode.RegistryPublish;
        targetPlotKey = plot.Canonical;
        headerLabel = plugin.HousingDetector.GetDisplayName(plot);
        nameInput = headerLabel;
        descInput = "";
        urlInput = "";
        listedInput = true;
        passwordProtectInput = false;
        passphraseInput = "";
        WindowName = "Publish new club##ClubFFXIVForm";
        IsOpen = true;
    }

    public void OpenRegistryEdit(string plotKey, ClubEntry entry)
    {
        mode = FormMode.RegistryEdit;
        targetPlotKey = plotKey;
        headerLabel = LabelForKey(plotKey, entry.DisplayName);
        nameInput = entry.DisplayName;
        descInput = entry.Description;
        urlInput = entry.StreamUrl;
        listedInput = entry.Listed;
        passwordProtectInput = !string.IsNullOrEmpty(entry.Passphrase);
        passphraseInput = entry.Passphrase ?? "";
        WindowName = "Edit club##ClubFFXIVForm";
        IsOpen = true;
    }

    private string LabelForKey(string plotKey, string fallbackName)
    {
        if (PlotKey.TryParse(plotKey, out var k))
            return plugin.HousingDetector.GetDisplayName(k);
        return fallbackName;
    }

    public override void Draw()
    {
        if (headerLabel.Length > 0)
        {
            ImGui.TextDisabled(headerLabel);
            ImGui.Spacing();
        }

        ImGui.TextUnformatted("Name:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##cfName", ref nameInput, 81);

        ImGui.Spacing();
        ImGui.TextUnformatted("Description:");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Optional. Shown to listeners in the Public Directory list\n" +
                "and in the URL permission prompt when someone first plays\n" +
                "your stream. Max 500 characters; line breaks supported.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##cfDesc", ref descInput, 501, new Vector2(-1, 80));

        ImGui.Spacing();
        ImGui.TextUnformatted("Stream URL:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##cfUrl", ref urlInput, 1024);

        if (mode == FormMode.RegistryPublish || mode == FormMode.RegistryEdit)
        {
            ImGui.Spacing();
            ImGui.Checkbox("Show in public directory", ref listedInput);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Adds your club to the Public Directory browse list.\n\n" +
                    "Unchecking only hides this club from the directory — anyone\n" +
                    "who knows your plot key or walks past still discovers it.\n" +
                    "For full privacy, don't publish (use a local override instead).");

            ImGui.Spacing();
            var wasProtected = passwordProtectInput;
            ImGui.Checkbox("Password-protect this club", ref passwordProtectInput);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Listeners need this passphrase before they can hear your\n" +
                    "club. We auto-generate a 6-word passphrase from the EFF\n" +
                    "long diceware list (~155 bits of entropy). Share it with\n" +
                    "your friends in Discord, etc. — anyone without it gets\n" +
                    "silence.\n\n" +
                    "The plaintext stays on your machine; the registry only\n" +
                    "stores an Argon2id hash + salt so we can verify listeners.");

            // Auto-generate on first toggle-on so the field is never empty
            // while the gate is enabled.
            if (passwordProtectInput && !wasProtected && string.IsNullOrEmpty(passphraseInput))
                passphraseInput = Passphrase.Generate();

            if (passwordProtectInput)
            {
                ImGui.Indent();
                ImGui.TextUnformatted("Passphrase:");
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##cfPassphrase", ref passphraseInput, 256, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.Button("Generate new", new Vector2(140, 0)))
                    passphraseInput = Passphrase.Generate();
                ImGui.SameLine();
                if (ImGui.Button("Copy", new Vector2(80, 0)))
                {
                    ImGui.SetClipboardText(passphraseInput);
                    plugin.Notify("ClubFFXIV", "Passphrase copied to clipboard.", NotificationType.Info);
                }
                ImGui.Unindent();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var submitLabel = mode switch
        {
            FormMode.LocalCreate => "Save",
            FormMode.LocalEdit => "Save",
            FormMode.RegistryPublish => "Publish",
            FormMode.RegistryEdit => "Save",
            _ => "Submit",
        };

        if (ImGui.Button(submitLabel, new Vector2(120, 0)))
            Submit();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 0)))
            IsOpen = false;
    }

    private void Submit()
    {
        var name = nameInput.Trim();
        var url = urlInput.Trim();
        var desc = descInput;
        var listed = listedInput;
        var key = targetPlotKey;

        if (string.IsNullOrEmpty(name))
        {
            plugin.Notify("ClubFFXIV", "Name cannot be empty.",
                NotificationType.Warning);
            return;
        }
        if (string.IsNullOrEmpty(url))
        {
            plugin.Notify("ClubFFXIV", "Stream URL is required.",
                NotificationType.Warning);
            return;
        }

        IsOpen = false;

        switch (mode)
        {
            case FormMode.LocalCreate:
            case FormMode.LocalEdit:
                plugin.UpsertSavedHouse(key, name, url, desc);
                plugin.Notify("ClubFFXIV",
                    $"Saved '{name}' locally.",
                    NotificationType.Success);
                break;

            case FormMode.RegistryPublish:
            {
                var passphraseToSend = passwordProtectInput ? passphraseInput : null;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await plugin.PublishHouseAsync(key, name, url, desc, listed, passphraseToSend);
                        plugin.Notify("ClubFFXIV",
                            listed
                                ? $"Published '{name}'."
                                : $"Published '{name}' (hidden from directory).",
                            NotificationType.Success);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error(ex, "Publish failed");
                        plugin.Notify("ClubFFXIV: publish failed",
                            ex.Message,
                            NotificationType.Error,
                            durationSeconds: 8);
                    }
                });
                break;
            }

            case FormMode.RegistryEdit:
            {
                var passphraseToSend = passwordProtectInput ? passphraseInput : null;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await plugin.RenamePublishedHouseAsync(key, name, url, desc, listed, passphraseToSend);
                        // If a local-only mirror also exists, sync the full
                        // editable set (name + URL + description) so the two
                        // copies don't drift. Indoor mode reads SavedHouses
                        // first, so failing to sync the URL here would mean
                        // the user's edit silently doesn't take effect inside
                        // their own house.
                        if (plugin.Config.SavedHouses.ContainsKey(key))
                            plugin.UpsertSavedHouse(key, name, url, desc);
                        plugin.Notify("ClubFFXIV",
                            listed
                                ? $"Updated '{name}'."
                                : $"Updated '{name}' (hidden from directory).",
                            NotificationType.Success);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error(ex, "Edit failed");
                        plugin.Notify("ClubFFXIV: edit failed",
                            ex.Message,
                            NotificationType.Error,
                            durationSeconds: 8);
                    }
                });
                break;
            }
        }
    }
}
