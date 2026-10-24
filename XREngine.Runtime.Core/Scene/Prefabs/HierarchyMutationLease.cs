namespace XREngine.Scene.Prefabs;

/// <summary>Reserves exact structural endpoints through callbacks without holding the admission lock.</summary>
internal readonly struct HierarchyMutationLease : IDisposable
{
    private readonly XRWorldObjectBase _owner;
    private readonly XRWorldObjectBase? _first;
    private readonly XRWorldObjectBase? _second;

    internal HierarchyMutationLease(XRWorldObjectBase owner, XRWorldObjectBase? first = null, XRWorldObjectBase? second = null)
    {
        _owner = owner;
        _first = ReferenceEquals(first, owner) ? null : first;
        _second = ReferenceEquals(second, owner) || ReferenceEquals(second, _first) ? null : second;
        lock (SceneRootCollection.MutationGate)
        {
            owner.RequireStructuralMutation();
            _first?.RequireStructuralMutation();
            _second?.RequireStructuralMutation();
            owner.EnterStructuralMutation();
            _first?.EnterStructuralMutation();
            _second?.EnterStructuralMutation();
        }
    }

    public void Dispose()
    {
        lock (SceneRootCollection.MutationGate)
        {
            _second?.ExitStructuralMutation();
            _first?.ExitStructuralMutation();
            _owner.ExitStructuralMutation();
        }
    }
}
