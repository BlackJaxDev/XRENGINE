namespace XREngine.Components;

/// <summary>
/// Generational slot arena that defers reuse until the renderer/backend reports
/// the last referencing epoch complete. Retirement is a structural operation.
/// </summary>
public sealed class PhysicsChainDeferredLifetimeArena<T>
{
    private readonly record struct PendingRetirement(PhysicsChainArenaHandle Handle, long RetireAfterEpoch);

    private readonly PhysicsChainSlotArena<T> _arena;
    private readonly List<PendingRetirement> _pendingRetirements = [];
    private long _completedEpoch = -1L;

    public PhysicsChainDeferredLifetimeArena(int initialCapacity = 16, int maximumCapacity = int.MaxValue)
        => _arena = new PhysicsChainSlotArena<T>(initialCapacity, maximumCapacity);

    public int Capacity => _arena.Capacity;
    public int LiveCount => _arena.LiveCount;
    public int PendingRetirementCount => _pendingRetirements.Count;
    public long CompletedEpoch => _completedEpoch;

    public PhysicsChainArenaHandle Allocate(T value)
        => _arena.Allocate(value);

    public bool TryAllocate(T value, out PhysicsChainArenaHandle handle)
        => _arena.TryAllocate(value, out handle);

    public bool TryGet(PhysicsChainArenaHandle handle, out T value)
        => _arena.TryGet(handle, out value);

    public bool Retire(PhysicsChainArenaHandle handle, long retireAfterEpoch)
    {
        if (retireAfterEpoch < 0L || !_arena.IsCurrent(handle) || IsPending(handle))
            return false;
        if (retireAfterEpoch <= _completedEpoch)
            return _arena.Free(handle);

        _pendingRetirements.Add(new PendingRetirement(handle, retireAfterEpoch));
        return true;
    }

    public int AdvanceCompletedEpoch(long completedEpoch)
    {
        if (completedEpoch < _completedEpoch)
            throw new ArgumentOutOfRangeException(nameof(completedEpoch), "Completed epochs must be monotonic.");

        _completedEpoch = completedEpoch;
        int released = 0;
        for (int i = _pendingRetirements.Count - 1; i >= 0; --i)
        {
            PendingRetirement pending = _pendingRetirements[i];
            if (pending.RetireAfterEpoch > completedEpoch)
                continue;

            if (_arena.Free(pending.Handle))
                ++released;
            _pendingRetirements.RemoveAt(i);
        }
        return released;
    }

    public PhysicsChainArenaSnapshot GetSnapshot()
        => _arena.GetSnapshot();

    private bool IsPending(PhysicsChainArenaHandle handle)
    {
        for (int i = 0; i < _pendingRetirements.Count; ++i)
            if (_pendingRetirements[i].Handle == handle)
                return true;
        return false;
    }
}
