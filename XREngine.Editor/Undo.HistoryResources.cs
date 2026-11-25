namespace XREngine.Editor;

public static partial class Undo
{
    private static UndoHistoryResource? _pendingHistoryResources;
    private static UndoHistoryResource? _failedHistoryResources;
    private static bool _drainingHistoryResources;

    internal static void RetainHistoryResource(ChangeScope scope, StructuralUndoStep step, UndoHistoryResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_sync)
        {
            if (scope.Completed || !_scopeStack.Contains(scope) || !scope.Steps.Contains(step))
                throw new InvalidOperationException("The resource requires its owning open structural step.");
            if (ReferenceEquals(resource.Owner, scope) && ReferenceEquals(resource.StructuralOwner, step))
                return;
            if (resource.Owner is not null || resource.Queued || resource.Releasing || resource.IsReleased || resource.ReleaseFailure is not null)
                throw new InvalidOperationException("The resource already has a history owner or a pending release.");
            (scope.Resources ??= []).Add(resource);
            resource.Owner = scope;
            resource.StructuralOwner = step;
        }
    }

    internal static void ReleaseRestoredHistoryResource(ChangeScope scope, StructuralUndoStep step, UndoHistoryResource resource)
    {
        lock (_sync)
        {
            if (scope.Completed || !_scopeStack.Contains(scope) || scope.Steps.Contains(step) ||
                !ReferenceEquals(resource.Owner, scope) || !ReferenceEquals(resource.StructuralOwner, step) ||
                scope.Resources?.Remove(resource) != true)
                throw new InvalidOperationException("Only the removed inverse can release its restored resource.");
            QueueHistoryResourceLocked(resource);
        }
        DrainHistoryResources();
    }

    /// <summary>Retries one retained release after its owner has repaired the reported failure.</summary>
    internal static bool RetryHistoryResourceRelease(UndoHistoryResource resource)
    {
        lock (_sync)
        {
            UndoHistoryResource? previous = null;
            UndoHistoryResource? current = _failedHistoryResources;
            while (current is not null && !ReferenceEquals(current, resource))
            {
                previous = current;
                current = current.Next;
            }
            if (current is null)
                return false;
            if (previous is null)
                _failedHistoryResources = current.Next;
            else
                previous.Next = current.Next;
            current.Next = null;
            current.ReleaseFailure = null;
            QueueHistoryResourceLocked(current);
        }
        DrainHistoryResources();
        return true;
    }

    private static void QueueHistoryResourceLocked(UndoHistoryResource resource)
    {
        resource.Owner = null;
        resource.StructuralOwner = null;
        if (resource.Queued || resource.IsReleased)
            return;
        resource.Queued = true;
        resource.Next = _pendingHistoryResources;
        _pendingHistoryResources = resource;
    }

    private static void ReleaseScopeResourcesLocked(ChangeScope scope)
    {
        if (scope.Resources is not { } resources)
            return;
        scope.Resources = null;
        foreach (UndoHistoryResource resource in resources)
            QueueHistoryResourceLocked(resource);
    }

    private static void TransferScopeResourcesLocked(ChangeScope scope, UndoAction action)
    {
        if (scope.Resources is not { } resources)
            return;
        action.Resources = resources;
        scope.Resources = null;
        foreach (UndoHistoryResource resource in resources)
        {
            resource.Owner = action;
            resource.StructuralOwner = null;
        }
    }

    private static void ClearHistoryStackLocked(Stack<UndoAction> stack)
    {
        while (stack.TryPop(out UndoAction? action))
        {
            if (action.Resources is not { } resources)
                continue;
            action.Resources = null;
            foreach (UndoHistoryResource resource in resources)
                QueueHistoryResourceLocked(resource);
        }
    }

    private static void DrainHistoryResources()
    {
        lock (_sync)
        {
            if (_drainingHistoryResources || _replayInProgress || _openStructuralMutations != 0 ||
                _pendingHistoryResources is null)
                return;
            _drainingHistoryResources = true;
            _openStructuralMutations++;
        }
        try
        {
            while (true)
            {
                UndoHistoryResource resource;
                lock (_sync)
                {
                    if (_openStructuralMutations != 1 || _pendingHistoryResources is not { } pending)
                        break;
                    resource = pending;
                    _pendingHistoryResources = pending.Next;
                    pending.Next = null;
                    pending.Queued = false;
                    pending.Releasing = true;
                }

                try
                {
                    // The callback must keep its own recovery owner until release succeeds.
                    // No history lock spans lifecycle callbacks, and replay remains held.
                    resource.Release();
                    lock (_sync)
                    {
                        resource.Releasing = false;
                        resource.IsReleased = true;
                    }
                }
                catch (Exception failure)
                {
                    lock (_sync)
                    {
                        resource.Releasing = false;
                        resource.ReleaseFailure = failure;
                        resource.Next = _failedHistoryResources;
                        _failedHistoryResources = resource;
                    }
                    try { Debug.LogWarning($"An Undo resource remains retained after release failed: {failure.Message}"); }
                    catch { }
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _drainingHistoryResources = false;
                _openStructuralMutations--;
            }
        }
        // Another thread can queue a release between the empty check and hold release.
        DrainHistoryResources();
    }
}
