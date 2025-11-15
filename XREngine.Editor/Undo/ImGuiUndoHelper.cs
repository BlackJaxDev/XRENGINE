using System;
using System.Collections.Generic;
using ImGuiNET;
using XREngine.Data.Core;

namespace XREngine.Editor;

internal static class ImGuiUndoHelper
{
    private sealed class ScopeInfo(Undo.ChangeScope scope, IDisposable? userInteraction, int frame)
    {
        public Undo.ChangeScope Scope { get; } = scope;
        public IDisposable? UserInteraction { get; } = userInteraction;
        public int LastFrameSeen { get; set; } = frame;
    }

    private static readonly Dictionary<uint, ScopeInfo> _scopes = new();
    private static int _lastPruneFrame = -1;

    public static void BeginFrame()
    {
        int frame = ImGui.GetFrameCount();
        if (_lastPruneFrame == frame)
            return;

        _lastPruneFrame = frame;

        List<uint>? expired = null;
        foreach (var pair in _scopes)
        {
            if (pair.Value.LastFrameSeen < frame - 1)
            {
                expired ??= [];
                expired.Add(pair.Key);
            }
        }

        if (expired is null)
            return;

        foreach (uint id in expired)
            CloseScope(id);
    }

    public static void UpdateScope(string description, XRBase? target)
    {
        uint itemId = ImGui.GetItemID();
        int frame = ImGui.GetFrameCount();

        if (ImGui.IsItemActivated())
            OpenScope(itemId, description, target, frame);

        if (ImGui.IsItemDeactivatedAfterEdit() || ImGui.IsItemDeactivated())
            CloseScope(itemId);

        if (_scopes.TryGetValue(itemId, out var info))
            info.LastFrameSeen = frame;
    }

    public static void CancelAll()
    {
        foreach (var info in _scopes.Values)
        {
            info.Scope.Dispose();
            info.UserInteraction?.Dispose();
        }
        _scopes.Clear();
        _lastPruneFrame = ImGui.GetFrameCount();
    }

    private static void OpenScope(uint itemId, string description, XRBase? target, int frame)
    {
        if (target is not null)
            Undo.Track(target);

        CloseScope(itemId);

        IDisposable? interaction = null;
        try
        {
            interaction = Undo.BeginUserInteraction();
            var scope = Undo.BeginChange(description);
            _scopes[itemId] = new ScopeInfo(scope, interaction, frame);
            interaction = null;
        }
        finally
        {
            interaction?.Dispose();
        }
    }

    private static void CloseScope(uint itemId)
    {
        if (!_scopes.TryGetValue(itemId, out var info))
            return;

        info.Scope.Dispose();
        info.UserInteraction?.Dispose();
        _scopes.Remove(itemId);
    }
}
