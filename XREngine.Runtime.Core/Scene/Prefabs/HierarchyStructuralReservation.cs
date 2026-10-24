namespace XREngine.Scene.Prefabs;

/// <summary>
/// Stops new structural mutations after all admitted operations finish.
/// This reservation does not by itself authorize object destruction.
/// </summary>
internal sealed class HierarchyStructuralReservation : IDisposable
{
    private readonly XRWorldObjectBase[] _objects;
    private bool _disposed;

    private HierarchyStructuralReservation(XRWorldObjectBase[] objects) => _objects = objects;

    internal static HierarchyStructuralReservation? TryAcquire(IReadOnlyList<XRWorldObjectBase> objects)
    {
        HashSet<XRWorldObjectBase> unique = new(ReferenceEqualityComparer.Instance);
        foreach (XRWorldObjectBase value in objects)
        {
            ArgumentNullException.ThrowIfNull(value);
            unique.Add(value);
        }
        HierarchyStructuralReservation reservation = new([.. unique]);
        lock (SceneRootCollection.MutationGate)
        {
            foreach (XRWorldObjectBase value in reservation._objects)
                if (value.HasStructuralMutation)
                    return null;
            foreach (XRWorldObjectBase value in reservation._objects)
                if (!value.TrySetStructuralReservation(reservation))
                {
                    foreach (XRWorldObjectBase installed in reservation._objects)
                        installed.ReleaseStructuralReservation(reservation);
                    return null;
                }
            // A lifetime operation can enter without this gate. If it entered before
            // installation, it must finish before any reserved operation can proceed.
            foreach (XRWorldObjectBase value in reservation._objects)
                if (value.HasActiveStructuralMutation)
                {
                    foreach (XRWorldObjectBase installed in reservation._objects)
                        installed.ReleaseStructuralReservation(reservation);
                    return null;
                }
        }
        return reservation;
    }

    public void Dispose()
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (XRWorldObjectBase value in _objects)
                value.ReleaseStructuralReservation(this);
        }
    }
}
