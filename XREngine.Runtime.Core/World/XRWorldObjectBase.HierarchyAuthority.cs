using XREngine.Scene;
using XREngine.Scene.Prefabs;

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

    internal bool HasStructuralMutation => _hierarchyMutationCount != 0 || _hierarchyReservation is not null;

    internal void RequireStructuralMutation()
    {
        if (_hierarchyReservation is not null)
            throw new InvalidOperationException("The exact hierarchy object is reserved for an ownership operation.");
    }

    internal void EnterStructuralMutation()
    {
        RequireStructuralMutation();
        SetField(ref _hierarchyMutationCount, checked(_hierarchyMutationCount + 1), publishNotifications: false);
    }

    internal void ExitStructuralMutation()
        => SetField(ref _hierarchyMutationCount, _hierarchyMutationCount - 1, publishNotifications: false);

    internal void SetStructuralReservation(HierarchyStructuralReservation reservation)
        => SetField(ref _hierarchyReservation, reservation, publishNotifications: false);

    internal void ReleaseStructuralReservation(HierarchyStructuralReservation reservation)
    {
        if (ReferenceEquals(_hierarchyReservation, reservation))
            SetField(ref _hierarchyReservation, null, publishNotifications: false);
    }

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
