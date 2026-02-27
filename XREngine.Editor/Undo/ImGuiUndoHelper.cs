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

    /// <summary>
    /// Call this at the beginning of each ImGui frame to automatically 
    /// close any undo scopes that have been left open for more than one frame,
    /// which likely indicates that the user has stopped interacting 
    /// with the associated ImGui item without properly closing the scope. 
    /// This helps prevent stale undo scopes from accumulating and ensures 
    /// that undo actions are properly finalized even if the user 
    /// doesn't interact with the UI in a perfectly consistent way.
    /// </summary>
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

    /// <summary>
    /// Call this from an ImGui item scope to automatically open and close an undo scope around user interactions with that item.
    /// </summary>
    /// <param name="description">The description of the undo action.</param>
    /// <param name="target">The target object for the undo action.</param>
    public static void TrackDragUndo(string description, XRBase? target)
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

    /// <summary>
    /// Closes all open undo scopes. Call this when the editor state changes in a way that would invalidate existing undo scopes, such as loading a new scene or performing an undo/redo action.
    /// </summary>
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

    /// <summary>
    /// Opens a new undo scope for the given item ID and target object. If a scope already exists for the item ID, it will be closed before opening the new scope.
    /// </summary>
    /// <param name="itemId">The ImGui item ID for the scope.</param>
    /// <param name="description">The description of the undo action.</param>
    /// <param name="target">The target object for the undo action.</param>
    /// <param name="frame">The current frame count.</param>
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

    /// <summary>
    /// Closes the undo scope associated with the given item ID, if one exists. This should be called when the user finishes interacting with an ImGui item to finalize the undo action.
    /// </summary>
    /// <param name="itemId">The ImGui item ID for the scope.</param>
    private static void CloseScope(uint itemId)
    {
        if (!_scopes.TryGetValue(itemId, out var info))
            return;

        info.Scope.Dispose();
        info.UserInteraction?.Dispose();
        _scopes.Remove(itemId);
    }
}
