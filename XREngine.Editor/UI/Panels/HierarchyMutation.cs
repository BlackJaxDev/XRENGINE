namespace XREngine.Editor;

/// <summary>Records a hierarchy inverse before callbacks and validates both ends of property replay.</summary>
internal static class HierarchyMutation
{
    public static void Execute(string description, Func<HierarchyMutationOperation> prepare,
        Action? onApplied = null, Func<bool>? ownerIsCurrent = null, Action? releaseRestoredCreation = null)
    {
        using var interaction = Undo.BeginUserInteraction();
        Undo.ChangeScope scope = Undo.BeginChange(description);
        HierarchyMutationOperation? operation = null;
        StructuralChangeHandle? opening = null;
        try
        {
            opening = Undo.BeginStructuralMutation(description,
                () => operation!.UndoMembership(),
                () => operation!.RedoMembership());
            try
            {
                operation = prepare();
                RequireOwner(ownerIsCurrent);
                Undo.TrackSceneNode(operation.Node);
                operation.PrepareInitial();
                operation.ApplyInitial();
                RequireOwner(ownerIsCurrent);
                onApplied?.Invoke();
                operation.MarkDirty();
                RequireOwner(ownerIsCurrent);
                operation.RequireAfter(includeProperties: true);
                operation.ReconcileProperties(opening);
            }
            catch
            {
                bool restored = operation is null || operation.RestoreInitial();
                if (operation is not null)
                {
                    try { operation.MarkDirty(); }
                    catch { }
                    // Dirty callbacks can change ownership too. Do not discard
                    // the inverse until the entire compensation is still valid.
                    if (restored)
                    {
                        try { operation.RequireBefore(includeProperties: true); }
                        catch { restored = false; }
                    }
                }
                if (restored)
                {
                    operation?.ReconcileProperties(opening);
                    opening.RemoveRestoredStep();
                    releaseRestoredCreation?.Invoke();
                }
                else
                {
                    operation!.CaptureFailure();
                    operation.ReconcileProperties(opening);
                    AddClosingGuard(description, operation);
                }
                throw;
            }
            AddClosingGuard(description, operation!);
        }
        finally
        {
            // History observers can request replay. Keep the hold through commit
            // so they cannot run an action before its command returns.
            try { scope.Dispose(); }
            finally { opening?.Dispose(); }
        }
    }

    private static void AddClosingGuard(string description, HierarchyMutationOperation operation)
    {
        using StructuralChangeHandle guard = Undo.BeginStructuralMutation(description,
            operation.RequireUndoEntry,
            operation.CompleteRedo);
    }

    private static void RequireOwner(Func<bool>? ownerIsCurrent)
    {
        if (ownerIsCurrent?.Invoke() == false)
            throw new InvalidOperationException("The hierarchy panel owner changed during the edit.");
    }
}
