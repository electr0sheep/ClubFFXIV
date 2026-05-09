using System;
using System.Collections.Generic;
using System.Numerics;
using ClubFFXIV.Network;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// Modal-ish prompt that asks the user for a club's passphrase. Opens when an
/// auto-play path hits a password-protected club (registry returned
/// PasswordRequired). On Submit the entered text is handed back via a
/// callback; the upstream code path is responsible for hashing it (with the
/// salt the registry returned) and re-fetching the URL.
///
/// Multiple prompts can queue (e.g., entering a ward with several locked
/// clubs nearby) — only one is shown at a time, in submission order.
/// </summary>
public sealed class ClubPassphrasePromptWindow : Window, IDisposable
{
    private readonly Queue<PendingPrompt> pending = new();
    private PendingPrompt? active;

    private string passphraseInput = "";
    private string statusMessage = "";

    public ClubPassphrasePromptWindow()
        : base("Club passphrase##ClubFFXIVPassphrasePrompt",
              ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize)
    {
        Size = new Vector2(440, 200);
        SizeCondition = ImGuiCond.Always;
        IsOpen = false;
    }

    public void Dispose() { }

    /// <summary>
    /// Queue a passphrase prompt. <paramref name="onSubmit"/> receives the
    /// raw entered text; the caller hashes + verifies. <paramref name="onCancel"/>
    /// fires if the user dismisses without entering anything (so the caller
    /// can mark the URL skipped and not re-prompt every tick).
    /// </summary>
    public void Prompt(
        string plotKey,
        ClubContext? context,
        Action<string> onSubmit,
        Action onCancel)
    {
        pending.Enqueue(new PendingPrompt(plotKey, context, onSubmit, onCancel));
    }

    /// <summary>
    /// Returns true if there's at least one pending or active prompt — used
    /// by upstream auto-play code to know whether it has already opened a
    /// prompt for this URL (and shouldn't re-open every framework tick).
    /// </summary>
    public bool HasPromptFor(string plotKey)
    {
        if (active is { } a && a.PlotKey == plotKey) return true;
        foreach (var p in pending)
            if (p.PlotKey == plotKey) return true;
        return false;
    }

    public void EnsureOpenIfPending()
    {
        if (active != null || pending.Count > 0) IsOpen = true;
    }

    public override void Draw()
    {
        // Promote the next pending prompt when no prompt is currently active.
        if (active == null && pending.Count > 0)
        {
            active = pending.Dequeue();
            passphraseInput = "";
            statusMessage = "";
        }

        if (active is not { } prompt)
        {
            IsOpen = false;
            return;
        }

        var clubName = prompt.Context?.ClubName ?? "(unnamed club)";
        ImGui.TextWrapped($"\"{clubName}\" is password-protected.");
        ImGui.Spacing();
        if (prompt.Context is { Description: var desc } && !string.IsNullOrWhiteSpace(desc))
        {
            ImGui.TextDisabled(desc);
            ImGui.Spacing();
        }

        ImGui.TextUnformatted("Passphrase:");
        ImGui.SetNextItemWidth(-1);
        var entered = ImGui.InputText("##passphrase", ref passphraseInput, 256,
            ImGuiInputTextFlags.EnterReturnsTrue);

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(UiColors.Error, statusMessage);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var canSubmit = !string.IsNullOrWhiteSpace(passphraseInput);
        if (!canSubmit) ImGui.BeginDisabled();
        var submitClicked = ImGui.Button("Submit", new Vector2(120, 0));
        if (!canSubmit) ImGui.EndDisabled();

        ImGui.SameLine();
        var cancelClicked = ImGui.Button("Skip", new Vector2(80, 0));

        // Enter-in-input-text counts as Submit too.
        if (canSubmit && (submitClicked || entered))
        {
            var entry = passphraseInput;
            // Don't dispose `active` here — the caller will tell us via
            // ReportFailure/Resolve whether the entered passphrase worked,
            // so we can either keep the prompt open with an error or move
            // on to the next pending one.
            try { prompt.OnSubmit(entry); }
            catch { /* never let UI work explode the host */ }
        }

        if (cancelClicked)
        {
            try { prompt.OnCancel(); } catch { }
            active = null;
        }
    }

    /// <summary>
    /// Called by the caller after the entered passphrase has been verified.
    /// Closes the active prompt and either pulls in the next pending one or
    /// hides the window.
    /// </summary>
    public void Resolve()
    {
        active = null;
        if (pending.Count == 0) IsOpen = false;
    }

    /// <summary>
    /// Called by the caller when the entered passphrase didn't verify.
    /// Keeps the prompt open with an error message so the user can retry.
    /// </summary>
    public void ReportFailure(string reason)
    {
        statusMessage = reason;
        passphraseInput = "";
    }

    private sealed record PendingPrompt(
        string PlotKey,
        ClubContext? Context,
        Action<string> OnSubmit,
        Action OnCancel);
}
