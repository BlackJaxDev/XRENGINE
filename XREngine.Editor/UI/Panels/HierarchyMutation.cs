namespace XREngine.Editor;

/// <summary>Records a hierarchy inverse before callbacks and validates both ends of property replay.</summary>
internal static class HierarchyMutation
{
    public static void Execute(string description, Func<HierarchyMutationOperation> prepare,
        Action? onApplied = null, Func<bool>? ownerIsCurrent = null, Action? releaseRestoredCreation = null,
        HierarchyCloneCustody? cloneCustody = null)
    {
        using var interaction = Undo.BeginUserInteraction();
        Undo.ChangeScope scope = Undo.BeginChange(description);
        HierarchyMutationOperation? operation = null;
        StructuralChangeHandle? opening = null;
        try
        {
            opening = Undo.BeginStructuralMutation(description,
                () => Replay(() =>
                {
                    operation!.UndoMembership();
                    cloneCustody?.MarkDetached();
                    operation.RequireBefore(includeProperties: true);
                }, cloneCustody),
                () => Replay(() => { cloneCustody?.BeginRedo(); operation!.RedoMembership(); }, cloneCustody));
            cloneCustody?.AttachHistory(opening);
            try
            {
                operation = prepare();
                cloneCustody?.RequireOperation(operation);
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
            catch (Exception failure)
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
                    try
                    {
                        operation?.ReconcileProperties(opening);
                        if (operation is not null)
                        {
                            cloneCustody?.MarkDetached();
                            operation.RequireBefore(includeProperties: true);
                        }
                    }
                    catch { restored = false; }
                }
                if (restored)
                {
                    opening.RemoveRestoredStep();
                    cloneCustody?.ReleaseRestored(opening);
                    releaseRestoredCreation?.Invoke();
                }
                else
                {
                    cloneCustody?.RetainFailure(failure);
                    operation!.CaptureFailure();
                    operation.ReconcileProperties(opening);
                    AddClosingGuard(description, operation, cloneCustody);
                }
                throw;
            }
            AddClosingGuard(description, operation!, cloneCustody);
            cloneCustody?.MarkApplied();
            operation!.RequireAfter(includeProperties: true);
        }
        finally
        {
            // History observers can request replay. Keep the hold through commit
            // so they cannot run an action before its command returns.
            try { scope.Dispose(); }
            finally
            {
                try { cloneCustody?.EndInitialAdmission(); }
                finally { opening?.Dispose(); }
            }
        }
    }

    private static void AddClosingGuard(string description, HierarchyMutationOperation operation,
        HierarchyCloneCustody? cloneCustody)
    {
        using StructuralChangeHandle guard = Undo.BeginStructuralMutation(description,
            () => Replay(() => { cloneCustody?.BeginUndo(); operation.RequireUndoEntry(); }, cloneCustody),
            () => Replay(() =>
            {
                operation.CompleteRedo();
                cloneCustody?.MarkApplied();
                operation.RequireAfter(includeProperties: true);
            }, cloneCustody));
    }

    private static void Replay(Action action, HierarchyCloneCustody? cloneCustody)
    {
        try
        {
            using var admission = cloneCustody?.AcquireAdmission();
            action();
        }
        catch (Exception failure)
        {
            cloneCustody?.RetainFailure(failure);
            throw;
        }
    }

    private static void RequireOwner(Func<bool>? ownerIsCurrent)
    {
        if (ownerIsCurrent?.Invoke() == false)
            throw new InvalidOperationException("The hierarchy panel owner changed during the edit.");
    }
}
