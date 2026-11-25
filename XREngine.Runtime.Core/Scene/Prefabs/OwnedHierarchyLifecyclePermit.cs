namespace XREngine.Scene.Prefabs;

/// <summary>Admits one exact reserved stop callback and rejects reuse.</summary>
internal sealed class OwnedHierarchyLifecyclePermit(
    OwnedHierarchyRetirement retirement, XRWorldObjectBase target, EOwnedHierarchyLifecycle kind) : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private int _state;
    internal OwnedHierarchyRetirement Retirement { get; } = retirement;
    internal XRWorldObjectBase Target { get; } = target;
    internal EOwnedHierarchyLifecycle Kind { get; } = kind;

    internal void Enter(XRWorldObjectBase target)
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_state != 0 || Environment.CurrentManagedThreadId != _thread || !ReferenceEquals(Target, target))
                throw new InvalidOperationException("The exact lifecycle permit cannot be reused or redirected.");
            Retirement.Validate();
            Target.EnterReservedHierarchyMutation(Retirement.Reservation);
            _state = 1;
        }
    }

    internal void Complete()
    {
        if (_state != 1 || Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("The exact lifecycle permit is not active.");
        Retirement.Validate();
        _state = 2;
    }

    public void Dispose()
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_state is not (1 or 2)) return;
            _state = 3;
            Target.ExitStructuralMutation();
        }
    }
}
