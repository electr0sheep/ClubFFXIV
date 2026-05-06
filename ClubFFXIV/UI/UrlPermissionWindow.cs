using System;
using System.Collections.Generic;
using System.Numerics;
using ClubFFXIV.Network;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ClubFFXIV.UI;

/// <summary>
/// Popup that asks the user whether to allow a URL whose host isn't on the
/// whitelist. Shows whichever URL is at the head of the pending queue;
/// dismissing one reveals the next.
/// </summary>
public sealed class UrlPermissionWindow : Window, IDisposable
{
    private readonly UrlPermissions permissions;
    private readonly Queue<PendingPrompt> pending = new();
    private readonly HashSet<string> tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private PendingPrompt? current;

    public UrlPermissionWindow(UrlPermissions permissions)
        : base("ClubFFXIV — Allow stream?##ClubFFXIVPerm",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.permissions = permissions;
        Size = new Vector2(520, 340);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
    }

    public void Dispose() { }

    /// <summary>
    /// Queue a permission prompt. onAllow fires if the user allows (URL or domain);
    /// onBlock fires if they block. Skip just dismisses without modifying lists.
    /// Repeated calls for the same URL while one is queued or showing are no-ops —
    /// auto-play paths fire every tick, and we don't want the queue (and its
    /// captured lambdas) to grow unbounded.
    /// Optional <paramref name="context"/> is rendered above the action buttons
    /// so the user can see "this URL is for: {ClubName}" with the description.
    /// </summary>
    public void Prompt(string url, Action onAllow, Action onBlock, ClubContext? context = null)
    {
        lock (gate)
        {
            if (!tracked.Add(url)) return;
            pending.Enqueue(new PendingPrompt(url, onAllow, onBlock, context));
        }
    }

    public override void Draw()
    {
        if (current == null)
        {
            lock (gate)
            {
                if (pending.Count > 0) current = pending.Dequeue();
            }
        }

        if (current == null)
        {
            IsOpen = false;
            return;
        }

        var c = current;
        ImGui.TextWrapped("A stream URL has been requested that isn't on your allow list:");
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.10f, 0.12f, 1f));
        ImGui.BeginChild("##permUrl", new Vector2(-1, 50), ImGuiChildFlags.Borders);
        ImGui.Spacing();
        ImGui.TextWrapped(c.Url);
        ImGui.EndChild();
        ImGui.PopStyleColor();

        var host = UrlPermissions.ExtractHost(c.Url) ?? "(unknown)";
        ImGui.Spacing();
        ImGui.TextDisabled($"Host: {host}");

        // Club context (when known): name + description. Best-effort — if the
        // caller didn't pass context and the URL doesn't match anything in our
        // local caches, this section is simply omitted.
        if (c.Context is { } ctx && !ctx.IsEmpty)
        {
            ImGui.Spacing();
            if (!string.IsNullOrWhiteSpace(ctx.ClubName))
            {
                ImGui.TextUnformatted("Club:");
                ImGui.SameLine();
                ImGui.TextWrapped(ctx.ClubName);
            }
            if (!string.IsNullOrWhiteSpace(ctx.Description))
            {
                ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.10f, 0.12f, 1f));
                ImGui.BeginChild("##permDesc", new Vector2(-1, 70), ImGuiChildFlags.Borders);
                ImGui.Spacing();
                ImGui.TextWrapped(ctx.Description);
                ImGui.EndChild();
                ImGui.PopStyleColor();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Allow this URL", new Vector2(140, 0)))
        {
            permissions.AllowUrl(c.Url);
            Resolve(c, c.OnAllow);
            return;
        }
        ImGui.SameLine();
        if (ImGui.Button($"Allow {host}", new Vector2(180, 0)))
        {
            permissions.AllowDomain(c.Url);
            Resolve(c, c.OnAllow);
            return;
        }
        ImGui.SameLine();
        if (ImGui.Button("Block (don't ask again)", new Vector2(180, 0)))
        {
            permissions.BlockUrl(c.Url);
            Resolve(c, c.OnBlock);
            return;
        }

        ImGui.Spacing();
        if (ImGui.Button("Skip (ask again later)"))
        {
            Resolve(c, c.OnBlock);
        }
    }

    private void Resolve(PendingPrompt c, Action callback)
    {
        lock (gate) tracked.Remove(c.Url);
        current = null;
        callback();
    }

    public bool HasPending
    {
        get
        {
            if (current != null) return true;
            lock (gate) return pending.Count > 0;
        }
    }

    /// <summary>Re-open if there are pending prompts. Called by Plugin per tick.</summary>
    public void EnsureOpenIfPending()
    {
        if (HasPending && !IsOpen) IsOpen = true;
    }

    private sealed record PendingPrompt(
        string Url, Action OnAllow, Action OnBlock, ClubContext? Context);
}
