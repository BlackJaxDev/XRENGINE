using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.UI;

namespace XREngine.Editor;

/// <summary>Publishes exact animation edits with guarded Undo and source dirtiness.</summary>
internal static class AnimationAuthoringTransaction
{
    private static readonly object ReservationGate = new();
    private static readonly HashSet<object> ReservedOwners =
        new(ReferenceEqualityComparer.Instance);

    public static bool Apply(object primaryOwner, IReadOnlyList<XRAsset> documents,
        string description, Action forward, Action reverse,
        Func<bool> isApplied, Func<bool> isRestored, Func<bool> isStillOwned,
        (XRBase Target, string Property)[]? ownedProperties = null,
        params object[] additionalOwners)
    {
        if (documents.Count == 0 || documents.Any(document =>
            document.IsDestroyed || document.IsDestroyQueued))
            return false;
        XRAsset[] sourceDocuments = [.. documents.Distinct<XRAsset>(ReferenceEqualityComparer.Instance)];
        (XRAsset Document, Guid Identity)[] documentIdentities = sourceDocuments
            .Select(document => (document, document.ID)).ToArray();
        object[] owners = [primaryOwner, .. additionalOwners];
        lock (ReservationGate)
        {
            if (owners.Any(ReservedOwners.Contains))
                return false;
            foreach (object owner in owners)
                ReservedOwners.Add(owner);
        }
        bool mutated = false;
        bool accepted = false;
        try
        {
            using IDisposable interaction = Undo.BeginUserInteraction();
            Undo.ChangeScope change = Undo.BeginChange(description);
            StructuralChangeHandle? handle = null;
            bool complete = false;
            bool settled = false;
            bool Owned() => documentIdentities.All(entry =>
                    !entry.Document.IsDestroyed && !entry.Document.IsDestroyQueued &&
                    entry.Document.ID == entry.Identity) && isStillOwned();
            void MarkDocumentsDirty(string failure)
            {
                foreach (XRAsset document in sourceDocuments)
                    try { document.MarkDirty(); }
                    catch (Exception exception)
                    { Debug.LogException(exception, failure); }
            }
            void Replay(Action action)
            {
                lock (ReservationGate)
                {
                    if (owners.Any(ReservedOwners.Contains))
                        throw new InvalidOperationException("Animation source is being edited.");
                    foreach (object owner in owners)
                        ReservedOwners.Add(owner);
                }
                try { action(); }
                finally
                {
                    try
                    {
                        MarkDocumentsDirty($"Could not mark '{description}' replay dirty.");
                    }
                    finally
                    {
                        lock (ReservationGate)
                            foreach (object owner in owners)
                                ReservedOwners.Remove(owner);
                    }
                }
            }
            void GuardedReverse()
            {
                Replay(() =>
                {
                    if (!Owned() || !isApplied())
                        throw new InvalidOperationException("Animation undo source changed ownership or state.");
                    reverse();
                    if (!Owned() || !isRestored())
                        throw new InvalidOperationException("Animation undo did not restore its exact source.");
                });
            }
            void GuardedForward()
            {
                Replay(() =>
                {
                    if (!Owned() || !isRestored())
                        throw new InvalidOperationException("Animation redo source changed ownership or state.");
                    forward();
                    if (!Owned() || !isApplied())
                        throw new InvalidOperationException("Animation redo did not reach its exact destination.");
                });
            }
            try
            {
                handle = Undo.BeginStructuralMutation(description,
                    GuardedReverse, GuardedForward);
                try
                {
                    try
                    {
                        if (!Owned() || !isRestored())
                            throw new InvalidOperationException("Animation edit source changed before mutation.");
                        mutated = true;
                        forward();
                        accepted = Owned() && isApplied();
                        if (!accepted)
                            throw new InvalidOperationException("Animation edit did not reach its exact destination.");
                        if (ownedProperties is not null)
                            foreach ((XRBase target, string property) in ownedProperties)
                                handle.RemoveOwnedProperty(target, property);
                        settled = true;
                    }
                    catch (Exception exception)
                    {
                        accepted = false;
                        bool canReverse = false;
                        try { canReverse = mutated && Owned() && isApplied(); }
                        catch (Exception checkException)
                        { Debug.LogException(checkException, $"Could not inspect '{description}' after failure."); }
                        if (canReverse)
                            try { reverse(); }
                            catch (Exception rollbackException)
                            { Debug.LogException(rollbackException, $"Could not reverse '{description}'."); }
                        bool restored = false;
                        try { restored = Owned() && isRestored(); }
                        catch (Exception checkException)
                        { Debug.LogException(checkException, $"Could not verify '{description}' rollback."); }
                        if (!mutated || restored)
                        {
                            if (mutated && ownedProperties is not null)
                                foreach ((XRBase target, string property) in ownedProperties)
                                    handle.RemoveOwnedProperty(target, property);
                            handle.RemoveRestoredStep();
                            settled = true;
                        }
                        else
                            Debug.UI($"[NativeAnimation] '{description}' has a conflicting owner or value; Undo is guarded for recovery.");
                        Debug.LogException(exception, $"Failed animation edit '{description}'.");
                    }
                }
                finally
                {
                    if (accepted)
                        Undo.RecordStructuralChange(description + " preflight",
                            () => { if (!Owned() || !isApplied())
                                throw new InvalidOperationException("Animation undo preflight rejected changed source."); },
                            () => { if (!Owned() || !isApplied())
                                throw new InvalidOperationException("Animation redo postflight rejected changed source."); });
                    else if (!settled)
                        Undo.RecordStructuralChange(description + " conflict guard",
                            () => throw new InvalidOperationException("Animation undo requires conflict recovery."),
                            () => throw new InvalidOperationException("Animation redo requires conflict recovery."));
                }
                complete = true;
            }
            finally
            {
                try { change.Dispose(); }
                catch (Exception exception)
                {
                    Debug.LogException(exception,
                        $"Could not publish '{description}' Undo history.");
                }
                finally
                {
                    try
                    {
                        if (mutated)
                            MarkDocumentsDirty($"Could not mark '{description}' source dirty.");
                    }
                    finally { handle?.Dispose(); }
                }
            }
            return accepted && complete;
        }
        finally
        {
            lock (ReservationGate)
            {
                foreach (object owner in owners)
                    ReservedOwners.Remove(owner);
            }
        }
    }
}
