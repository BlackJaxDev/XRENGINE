namespace XREngine.Scene.Prefabs;

/// <summary>Reserves an already materialized set of structural endpoints through callbacks.</summary>
internal readonly struct HierarchyMutationRangeLease : IDisposable
{
    private readonly IReadOnlyList<XRWorldObjectBase> _objects;

    internal HierarchyMutationRangeLease(IReadOnlyList<XRWorldObjectBase> objects)
    {
        _objects = objects;
        lock (SceneRootCollection.MutationGate)
        {
            foreach (XRWorldObjectBase value in objects) value?.RequireStructuralMutation();
            foreach (XRWorldObjectBase value in objects) value?.EnterStructuralMutation();
        }
    }

    public void Dispose()
    {
        lock (SceneRootCollection.MutationGate)
            foreach (XRWorldObjectBase value in _objects) value?.ExitStructuralMutation();
    }
}
