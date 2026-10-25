using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Data.Core;

namespace XREngine;

public abstract partial class XRWorldObjectBase
{
    private int _hierarchyMutationCount;
    private HierarchyStructuralReservation? _hierarchyReservation;
    private HierarchyLifetimeAdmission? _hierarchyLifetimeAdmission;
    private OwnedHierarchyDestructionPermit? _ownedDestructionPermit;
    private Dictionary<IHierarchyListAuthority, int>? _hierarchyListOwners;
    private int _hasEnteredStructuralList;

    internal bool HasEnteredStructuralList => Volatile.Read(ref _hasEnteredStructuralList) != 0;

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
    internal bool HasStructuralMutation => HasActiveStructuralMutation ||
        Volatile.Read(ref _hierarchyReservation) is not null || Volatile.Read(ref _hierarchyLifetimeAdmission) is not null;

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

    internal void EnterReservedHierarchyMutation(HierarchyStructuralReservation reservation)
    {
        if (!ReferenceEquals(Volatile.Read(ref _hierarchyReservation), reservation))
            throw new InvalidOperationException("The removal does not own this exact structural reservation.");
        Interlocked.Increment(ref _hierarchyMutationCount);
    }

    internal void SetOwnedDestructionPermit(OwnedHierarchyDestructionPermit permit)
    {
        if (!ReferenceEquals(permit.Target, this) ||
            !ReferenceEquals(Volatile.Read(ref _hierarchyReservation), permit.Retirement.Reservation) ||
            Interlocked.CompareExchange(ref _ownedDestructionPermit, permit, null) is not null)
            throw new InvalidOperationException("The destruction permit does not own this exact target.");
    }

    internal void ClearOwnedDestructionPermit(OwnedHierarchyDestructionPermit permit)
        => Interlocked.CompareExchange(ref _ownedDestructionPermit, null, permit);

    internal OwnedHierarchyRetirement? GetOwnedRetirement()
    {
        OwnedHierarchyDestructionPermit? permit = Volatile.Read(ref _ownedDestructionPermit);
        return permit?.Active == true ? permit.Retirement : null;
    }

    internal bool UsesCanonicalOwnedDestruction()
    {
        // This cold admission check allocates two bound delegates. Their dispatch
        // identity proves the selected implementation without reflected metadata.
        Action<bool> dispatched = Destroy;
        Action<bool> canonical = base.Destroy;
        return dispatched == canonical;
    }

    internal bool TrySetStructuralReservation(HierarchyStructuralReservation reservation)
        => Interlocked.CompareExchange(ref _hierarchyReservation, reservation, null) is null;

    internal void ReleaseStructuralReservation(HierarchyStructuralReservation reservation)
    {
        Interlocked.CompareExchange(ref _hierarchyReservation, null, reservation);
    }

    internal bool TrySetLifetimeAdmission(HierarchyLifetimeAdmission admission)
        => Interlocked.CompareExchange(ref _hierarchyLifetimeAdmission, admission, null) is null;

    internal void ReleaseLifetimeAdmission(HierarchyLifetimeAdmission admission)
        => Interlocked.CompareExchange(ref _hierarchyLifetimeAdmission, null, admission);

    internal void EnterLinkedListLifetimeMutation()
    {
        Interlocked.Increment(ref _hierarchyMutationCount);
        if (Volatile.Read(ref _hierarchyReservation) is null && Volatile.Read(ref _hierarchyLifetimeAdmission) is null)
            return;
        Interlocked.Decrement(ref _hierarchyMutationCount);
        throw new InvalidOperationException("A reserved structural list cannot change lifetime.");
    }

    internal void ExitLinkedListLifetimeMutation() => Interlocked.Decrement(ref _hierarchyMutationCount);

    protected sealed override void OnLifetimeMutationBeginning(EObjectLifetimeMutation kind)
    {
        // Publication can hold the cache lock. Admission uses atomics and never waits
        // on the structural gate. Reservation installation rechecks this counter.
        Interlocked.Increment(ref _hierarchyMutationCount);
        try
        {
            if (Volatile.Read(ref _hierarchyLifetimeAdmission) is not null)
                throw new InvalidOperationException("The clone edit holds this exact object's lifetime admission.");
            if (Volatile.Read(ref _hierarchyReservation) is { } reservation)
            {
                OwnedHierarchyDestructionPermit? permit = Volatile.Read(ref _ownedDestructionPermit);
                if (kind != EObjectLifetimeMutation.DestroyNow || permit is null ||
                    !ReferenceEquals(permit.Retirement.Reservation, reservation) || !permit.TryEnter())
                    throw new InvalidOperationException("The reserved hierarchy rejects this lifetime mutation.");
            }
        }
        catch
        {
            Interlocked.Decrement(ref _hierarchyMutationCount);
            throw;
        }
    }

    protected sealed override void OnLifetimeMutationAdmitted(EObjectLifetimeMutation kind, long previous, long current)
    {
        if (kind == EObjectLifetimeMutation.DestroyNow && Volatile.Read(ref _ownedDestructionPermit) is { } permit)
            permit.AdvanceLifetime(previous, current);
    }

    protected sealed override void OnLifetimeMutationEnded(EObjectLifetimeMutation kind)
    {
        try
        {
            if (kind == EObjectLifetimeMutation.DestroyNow && Volatile.Read(ref _ownedDestructionPermit) is { } permit)
                permit.Exit();
        }
        finally { Interlocked.Decrement(ref _hierarchyMutationCount); }
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
        Volatile.Write(ref _hasEnteredStructuralList, 1);
    }

    internal void RemoveHierarchyListOwner(IHierarchyListAuthority authority)
    {
        if (_hierarchyListOwners is null || !_hierarchyListOwners.TryGetValue(authority, out int count))
            throw new InvalidOperationException("The structural list occurrence has no matching owner.");
        if (count == 1) _hierarchyListOwners.Remove(authority);
        else _hierarchyListOwners[authority] = count - 1;
    }
}
