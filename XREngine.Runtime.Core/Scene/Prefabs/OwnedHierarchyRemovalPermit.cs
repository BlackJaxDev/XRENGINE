namespace XREngine.Scene.Prefabs;

/// <summary>Holds one exact removal through its callbacks. Public mutations cannot use it.</summary>
internal sealed class OwnedHierarchyRemovalPermit : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private int _state;

    internal OwnedHierarchyRemovalPermit(OwnedHierarchyRetirement retirement, XRWorldObjectBase owner,
        EOwnedHierarchyRemoval kind, object? slot, XRWorldObjectBase removed, int index = -1)
    {
        Retirement = retirement;
        Owner = owner;
        Kind = kind;
        Slot = slot;
        Removed = removed;
        Index = index;
    }

    internal OwnedHierarchyRetirement Retirement { get; }
    internal XRWorldObjectBase Owner { get; }
    internal EOwnedHierarchyRemoval Kind { get; }
    internal object? Slot { get; }
    internal XRWorldObjectBase Removed { get; }
    internal int Index { get; }

    internal void Enter(XRWorldObjectBase owner, EOwnedHierarchyRemoval kind, object? slot, XRWorldObjectBase removed)
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_state != 0 || Environment.CurrentManagedThreadId != _thread || !ReferenceEquals(owner, Owner) ||
                kind != Kind || !ReferenceEquals(slot, Slot) || !ReferenceEquals(removed, Removed))
                throw new InvalidOperationException("The exact removal permit cannot be reused or redirected.");
            Retirement.Validate();
            Owner.EnterReservedHierarchyMutation(Retirement.Reservation);
            try { Removed.EnterReservedHierarchyMutation(Retirement.Reservation); }
            catch { Owner.ExitStructuralMutation(); throw; }
            _state = 1;
        }
    }

    internal void RequireCommit()
    {
        if (_state != 1 || Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("The removal permit has no remaining commit.");
        Retirement.Validate();
    }

    internal void Commit()
    {
        if (_state != 1 || Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("The removal permit has already committed.");
        Retirement.Advance(this);
        _state = 2;
    }

    public void Dispose()
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_state is not (1 or 2)) return;
            _state = 3;
            Removed.ExitStructuralMutation();
            Owner.ExitStructuralMutation();
        }
    }
}
