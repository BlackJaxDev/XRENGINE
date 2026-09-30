namespace XREngine.Components;

/// <summary>
/// Geometrically growing generational slot arena used by world-owned
/// physics-chain runtime records. Allocation and free are structural-boundary
/// operations; frame workers access already validated slots directly.
/// </summary>
public sealed class PhysicsChainSlotArena<T>
{
    private T[] _items;
    private uint[] _generations;
    private bool[] _occupied;
    private readonly Stack<int> _freeSlots = [];
    private int _nextUnusedSlot;

    public PhysicsChainSlotArena(int initialCapacity = 16, int maximumCapacity = int.MaxValue)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        if (maximumCapacity < 1 || initialCapacity > maximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(maximumCapacity));

        int capacity = RoundCapacity(initialCapacity);
        if (capacity > maximumCapacity)
            capacity = maximumCapacity;
        _items = new T[capacity];
        _generations = new uint[capacity];
        _occupied = new bool[capacity];
        MaximumCapacity = maximumCapacity;
    }

    public int Capacity => _items.Length;
    public int MaximumCapacity { get; }
    public int LiveCount { get; private set; }
    public int GrowthCount { get; private set; }
    public float FragmentationRebuildThreshold { get; set; } = 0.5f;
    public bool ShouldRecommendRebuild
        => _nextUnusedSlot > 0
            && GetSnapshot().FragmentationRatio >= Math.Clamp(FragmentationRebuildThreshold, 0.0f, 1.0f);

    public PhysicsChainArenaHandle Allocate(T value)
    {
        if (TryAllocate(value, out PhysicsChainArenaHandle handle))
            return handle;
        throw new PhysicsChainArenaCapacityException(MaximumCapacity);
    }

    public bool TryAllocate(T value, out PhysicsChainArenaHandle handle)
    {
        int slot;
        if (!_freeSlots.TryPop(out slot))
        {
            if (_nextUnusedSlot == MaximumCapacity)
            {
                handle = PhysicsChainArenaHandle.Invalid;
                return false;
            }
            if (_nextUnusedSlot == Capacity)
                Grow();
            slot = _nextUnusedSlot++;
        }

        uint generation = _generations[slot];
        if (generation == 0u)
            generation = 1u;

        _generations[slot] = generation;
        _items[slot] = value;
        _occupied[slot] = true;
        ++LiveCount;
        handle = new PhysicsChainArenaHandle(slot, generation);
        return true;
    }

    public bool Free(PhysicsChainArenaHandle handle)
    {
        if (!IsCurrent(handle))
            return false;

        int slot = handle.Slot;
        _items[slot] = default!;
        _occupied[slot] = false;
        _generations[slot] = NextGeneration(_generations[slot]);
        _freeSlots.Push(slot);
        --LiveCount;
        return true;
    }

    public bool TryGet(PhysicsChainArenaHandle handle, out T value)
    {
        if (!IsCurrent(handle))
        {
            value = default!;
            return false;
        }

        value = _items[handle.Slot];
        return true;
    }

    public bool TrySet(PhysicsChainArenaHandle handle, T value)
    {
        if (!IsCurrent(handle))
            return false;

        _items[handle.Slot] = value;
        return true;
    }

    public ref T GetReference(PhysicsChainArenaHandle handle)
    {
        if (!IsCurrent(handle))
            throw new InvalidOperationException("The physics-chain arena handle is stale or invalid.");

        return ref _items[handle.Slot];
    }

    public bool IsCurrent(PhysicsChainArenaHandle handle)
        => handle.IsValid
            && (uint)handle.Slot < (uint)_nextUnusedSlot
            && _occupied[handle.Slot]
            && _generations[handle.Slot] == handle.Generation;

    public PhysicsChainArenaSnapshot GetSnapshot()
    {
        int allocatedSlots = _nextUnusedSlot;
        int freeSlotCount = allocatedSlots - LiveCount;
        float fragmentation = allocatedSlots > 0
            ? (float)freeSlotCount / allocatedSlots
            : 0.0f;
        return new PhysicsChainArenaSnapshot(
            Capacity,
            LiveCount,
            freeSlotCount,
            GrowthCount,
            fragmentation);
    }

    private void Grow()
    {
        int newCapacity = Capacity == 0
            ? 1
            : Capacity >= (MaximumCapacity + 1L) / 2L ? MaximumCapacity : Capacity * 2;
        Array.Resize(ref _items, newCapacity);
        Array.Resize(ref _generations, newCapacity);
        Array.Resize(ref _occupied, newCapacity);
        ++GrowthCount;
    }

    private static int RoundCapacity(int requestedCapacity)
    {
        if (requestedCapacity <= 1)
            return requestedCapacity;

        int capacity = 1;
        while (capacity < requestedCapacity)
            capacity = checked(capacity * 2);
        return capacity;
    }

    private static uint NextGeneration(uint generation)
    {
        unchecked
        {
            ++generation;
        }

        return generation == 0u ? 1u : generation;
    }
}
