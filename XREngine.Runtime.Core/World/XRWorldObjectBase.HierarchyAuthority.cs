using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Data.Core;

namespace XREngine;

public abstract partial class XRWorldObjectBase
{
    private int _hierarchyMutationCount;
    private HierarchyStructuralReservation? _hierarchyReservation;
    private Dictionary<IHierarchyListAuthority, int>? _hierarchyListOwners;

    /// <summary>Copies exact structural-list ownership at a mutation or release boundary.</summary>
    public HierarchyListMembership[] CaptureHierarchyListMemberships()
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_hierarchyListOwners is null || _hierarchyListOwners.Count == 0)
                return [];
            HierarchyListMembership[] result = new HierarchyListMembership[_hierarchyListOwners.Count];
            int index = 0;
            foreach (var pair in _hierarchyListOwners)
                result[index++] = new(pair.Key.Collection, pair.Key.Owner, pair.Value, pair.Key.IsCurrent);
            return result;
        }
    }

    internal bool HasActiveStructuralMutation => Volatile.Read(ref _hierarchyMutationCount) != 0;
    internal bool HasStructuralMutation => HasActiveStructuralMutation || Volatile.Read(ref _hierarchyReservation) is not null;

    internal void RequireStructuralMutation()
    {
        if (Volatile.Read(ref _hierarchyReservation) is not null)
            throw new InvalidOperationException("The exact hierarchy object is reserved for an ownership operation.");
    }

    internal void EnterStructuralMutation()
    {
        RequireStructuralMutation();
        Interlocked.Increment(ref _hierarchyMutationCount);
    }

    internal void ExitStructuralMutation()
        => Interlocked.Decrement(ref _hierarchyMutationCount);

    internal bool TrySetStructuralReservation(HierarchyStructuralReservation reservation)
        => Interlocked.CompareExchange(ref _hierarchyReservation, reservation, null) is null;

    internal void ReleaseStructuralReservation(HierarchyStructuralReservation reservation)
    {
        Interlocked.CompareExchange(ref _hierarchyReservation, null, reservation);
    }

    protected sealed override void OnLifetimeMutationBeginning(EObjectLifetimeMutation kind)
    {
        // Publication can hold the cache lock. Admission uses atomics and never waits
        // on the structural gate. Reservation installation rechecks this counter.
        Interlocked.Increment(ref _hierarchyMutationCount);
        try { RequireStructuralMutation(); }
        catch
        {
            Interlocked.Decrement(ref _hierarchyMutationCount);
            throw;
        }
    }

    protected sealed override void OnLifetimeMutationEnded(EObjectLifetimeMutation kind)
        => Interlocked.Decrement(ref _hierarchyMutationCount);

    internal void PrepareHierarchyListOwner(IHierarchyListAuthority authority)
    {
        _hierarchyListOwners ??= new(ReferenceEqualityComparer.Instance);
        if (!_hierarchyListOwners.ContainsKey(authority))
            _hierarchyListOwners.EnsureCapacity(checked(_hierarchyListOwners.Count + 1));
    }

    internal void AddHierarchyListOwner(IHierarchyListAuthority authority)
    {
        _hierarchyListOwners!.TryGetValue(authority, out int count);
        _hierarchyListOwners[authority] = checked(count + 1);
    }

    internal void RemoveHierarchyListOwner(IHierarchyListAuthority authority)
    {
        if (_hierarchyListOwners is null || !_hierarchyListOwners.TryGetValue(authority, out int count))
            throw new InvalidOperationException("The structural list occurrence has no matching owner.");
        if (count == 1) _hierarchyListOwners.Remove(authority);
        else _hierarchyListOwners[authority] = count - 1;
    }
}
