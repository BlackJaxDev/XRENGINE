using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Shares exact scene visibility and unload history between native and ImGui panels.</summary>
internal static class HierarchySceneActions
{
    private static readonly Lock ReservationGate = new();
    private static readonly HashSet<object> ReservedOwners = new(ReferenceEqualityComparer.Instance);

    internal static bool SetVisibility(XRScene scene, RuntimeWorld world, bool visible)
        => scene.IsVisible != visible && Apply(scene, world, unload: false, visible);

    internal static bool Unload(XRScene scene, RuntimeWorld world)
        => Apply(scene, world, unload: true, visible: false);

    private static bool Apply(XRScene scene, RuntimeWorld world, bool unload, bool visible)
    {
        if (!TryReserve(scene, world)) return false;
        string description = unload ? "Unload Scene" : "Toggle Scene Visibility";
        try
        {
            HierarchySceneEdit edit;
            try { edit = new(scene, world, unload, visible); }
            catch (Exception exception)
            {
                Debug.LogException(exception, $"Could not capture '{description}'.");
                return false;
            }
            using IDisposable interaction = Undo.BeginUserInteraction();
            Undo.ChangeScope change = Undo.BeginChange(description);
            StructuralChangeHandle? handle = null;
            bool accepted = false;
            try
            {
                Undo.TrackScene(scene);
                handle = Undo.BeginStructuralMutation(description,
                    () => Replay(edit, scene, world, forward: false),
                    () => Replay(edit, scene, world, forward: true));
                try
                {
                    try { edit.Run(forward: true); }
                    finally { edit.MarkDirty(); }
                    edit.RequireCompleted(applied: true);
                    edit.ReconcileOwnedProperties(handle);
                    Undo.RecordStructuralChange(description + " preflight",
                        () => RequireApplied(edit), () => RequireApplied(edit));
                    accepted = true;
                }
                catch (Exception exception)
                {
                    if (edit.IsRestored())
                    {
                        edit.ReconcileOwnedProperties(handle);
                        handle.RemoveRestoredStep();
                    }
                    else
                        Undo.RecordStructuralChange(description + " conflict guard",
                            () => throw new InvalidOperationException("The scene action requires conflict recovery."),
                            () => throw new InvalidOperationException("The scene action requires conflict recovery."));
                    Debug.LogException(exception, $"Could not complete '{description}'.");
                }
            }
            finally
            {
                try { change.Dispose(); }
                finally { handle?.Dispose(); }
            }
            return accepted;
        }
        finally { Release(scene, world); }
    }

    private static void Replay(HierarchySceneEdit edit, XRScene scene, RuntimeWorld world, bool forward)
    {
        if (!TryReserve(scene, world))
            throw new InvalidOperationException("The scene or its runtime world already has an active editor action.");
        try
        {
            try { edit.Run(forward); }
            finally { edit.MarkDirty(); }
            edit.RequireCompleted(forward);
        }
        finally { Release(scene, world); }
    }

    private static void RequireApplied(HierarchySceneEdit edit)
    {
        if (!edit.IsApplied())
            throw new InvalidOperationException("The scene action no longer owns its applied state.");
    }

    private static bool TryReserve(XRScene scene, RuntimeWorld world)
    {
        lock (ReservationGate)
        {
            if (ReservedOwners.Contains(scene) || ReservedOwners.Contains(world)) return false;
            ReservedOwners.Add(scene);
            ReservedOwners.Add(world);
            return true;
        }
    }

    private static void Release(XRScene scene, RuntimeWorld world)
    {
        lock (ReservationGate)
        {
            ReservedOwners.Remove(scene);
            ReservedOwners.Remove(world);
        }
    }
}
