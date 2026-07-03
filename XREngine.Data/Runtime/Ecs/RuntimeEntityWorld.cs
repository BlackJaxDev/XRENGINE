using XREngine.Data.Runtime.Collections;
using XREngine.Data.Runtime.Memory;

namespace XREngine.Data.Runtime.Ecs;

public readonly record struct RuntimeEntity(int Index, int Generation)
{
    public bool IsValid => Index >= 0 && Generation > 0;
}

public interface IRuntimeComponent
{
}

public enum RuntimeSystemPhase
{
    InputSample,
    NetworkReceive,
    Prediction,
    AuthoritativeSimulation,
    Animation,
    IK,
    PhysicsBridge,
    ReplicationBuild,
    Interpolation,
    RenderPrepare,
    GpuUpload,
    Diagnostics,
}

public interface IRuntimeSystem
{
    RuntimeSystemPhase Phase { get; }
    long AllocationBudgetBytes { get; }
    void Execute(ref RuntimeSystemContext context);
}

public ref struct RuntimeSystemContext
{
    public RuntimeSystemContext(RuntimeEntityWorld world, FrameScratchAllocator scratch)
        : this(world, scratch, string.Empty, default, 0L)
    {
    }

    public RuntimeSystemContext(
        RuntimeEntityWorld world,
        FrameScratchAllocator scratch,
        string systemName,
        RuntimeSystemPhase phase,
        long allocationBudgetBytes)
    {
        World = world;
        Scratch = scratch;
        SystemName = systemName;
        Phase = phase;
        AllocationBudgetBytes = allocationBudgetBytes;
    }

    public RuntimeEntityWorld World { get; }
    public FrameScratchAllocator Scratch { get; }
    public string SystemName { get; }
    public RuntimeSystemPhase Phase { get; }
    public long AllocationBudgetBytes { get; }
}

public struct RuntimeSystemAllocationStats
{
    public long LastBytes { get; private set; }
    public double AverageBytes { get; private set; }
    public long MaxBytes { get; private set; }
    public long SampleCount { get; private set; }
    public long OverBudgetCount { get; private set; }

    public void AddSample(long bytes, long budgetBytes)
    {
        if (bytes < 0L)
            bytes = 0L;

        LastBytes = bytes;
        MaxBytes = Math.Max(MaxBytes, bytes);
        SampleCount++;
        AverageBytes += (bytes - AverageBytes) / SampleCount;
        if (budgetBytes >= 0L && bytes > budgetBytes)
            OverBudgetCount++;
    }
}

public static class RuntimeSystemRunner
{
    public static void Execute(
        RuntimeEntityWorld world,
        IRuntimeSystem system,
        FrameScratchAllocator scratch,
        ref RuntimeSystemAllocationStats allocationStats)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(scratch);

        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        RuntimeSystemContext context = new(
            world,
            scratch,
            system.GetType().Name,
            system.Phase,
            system.AllocationBudgetBytes);

        system.Execute(ref context);
        allocationStats.AddSample(
            GC.GetAllocatedBytesForCurrentThread() - startBytes,
            system.AllocationBudgetBytes);
    }
}

public readonly record struct RuntimeRange(int Start, int End)
{
    public int Count => Math.Max(0, End - Start);
}

public static class RuntimeRangePartitioner
{
    public static int Partition(
        int itemCount,
        int maxRanges,
        int minItemsPerRange,
        Span<RuntimeRange> destination)
    {
        if (itemCount <= 0 || maxRanges <= 0 || destination.Length == 0)
            return 0;

        int clampedMin = Math.Max(1, minItemsPerRange);
        int desired = Math.Min(maxRanges, destination.Length);
        int rangeCount = Math.Min(desired, Math.Max(1, (itemCount + clampedMin - 1) / clampedMin));
        int baseSize = itemCount / rangeCount;
        int remainder = itemCount % rangeCount;
        int start = 0;

        for (int i = 0; i < rangeCount; i++)
        {
            int size = baseSize + (i < remainder ? 1 : 0);
            destination[i] = new RuntimeRange(start, start + size);
            start += size;
        }

        return rangeCount;
    }
}

public sealed class RuntimeEntityWorld
{
    private readonly Dictionary<Type, IRuntimeComponentStore> _stores = [];
    private readonly Dictionary<Type, object> _lookups = [];
    private int[] _generations = [];
    private int[] _free = [];
    private int _entityCount;
    private int _freeCount;

    public int EntityCapacity => _generations.Length;
    public int EntityCount => _entityCount - _freeCount;
    public int EntityCapacityGrowthCount { get; private set; }
    public int StructuralChangeCount { get; private set; }

    public void EnsureEntityCapacity(int capacity)
    {
        if (capacity <= _generations.Length)
            return;

        int newCapacity = _generations.Length == 0 ? 64 : _generations.Length;
        while (newCapacity < capacity)
            newCapacity *= 2;

        Array.Resize(ref _generations, newCapacity);
        Array.Resize(ref _free, newCapacity);
        EntityCapacityGrowthCount++;
    }

    public RuntimeEntity CreateEntity()
    {
        int index;
        if (_freeCount > 0)
        {
            index = _free[--_freeCount];
            _free[_freeCount] = 0;
        }
        else
        {
            EnsureEntityCapacity(_entityCount + 1);
            index = _entityCount++;
        }

        int generation = _generations[index] + 1;
        if (generation <= 0)
            generation = 1;
        _generations[index] = generation;
        StructuralChangeCount++;
        return new RuntimeEntity(index, generation);
    }

    public bool DestroyEntity(RuntimeEntity entity)
    {
        if (!IsAlive(entity))
            return false;

        foreach (IRuntimeComponentStore store in _stores.Values)
            store.RemoveEntity(entity);

        _generations[entity.Index]++;
        if (_generations[entity.Index] <= 0)
            _generations[entity.Index] = 1;

        EnsureEntityCapacity(entity.Index + 1);
        _free[_freeCount++] = entity.Index;
        StructuralChangeCount++;
        return true;
    }

    public bool IsAlive(RuntimeEntity entity)
        => entity.Index >= 0 &&
           entity.Index < _entityCount &&
           entity.Index < _generations.Length &&
           _generations[entity.Index] == entity.Generation;

    public RuntimeComponentStore<T> Store<T>()
        where T : unmanaged, IRuntimeComponent
    {
        Type type = typeof(T);
        if (_stores.TryGetValue(type, out IRuntimeComponentStore? existing))
            return (RuntimeComponentStore<T>)existing;

        RuntimeComponentStore<T> store = new(this);
        _stores.Add(type, store);
        return store;
    }

    public EntityLookup<TKey> Lookup<TKey>()
        where TKey : notnull
    {
        Type type = typeof(TKey);
        if (_lookups.TryGetValue(type, out object? existing))
            return (EntityLookup<TKey>)existing;

        EntityLookup<TKey> lookup = new(this);
        _lookups.Add(type, lookup);
        return lookup;
    }

    internal int CurrentGeneration(int index)
        => (uint)index < (uint)_generations.Length ? _generations[index] : 0;
}

public sealed class RuntimeComponentStore<T> : IRuntimeComponentStore
    where T : unmanaged, IRuntimeComponent
{
    private readonly RuntimeEntityWorld _world;
    private RuntimeEntity[] _denseEntities = [];
    private T[] _denseComponents = [];
    private int[] _sparse = [];
    private readonly HotBitSet _dirty = new();
    private int _dirtyMin = int.MaxValue;
    private int _dirtyMax = -1;

    internal RuntimeComponentStore(RuntimeEntityWorld world)
    {
        _world = world;
    }

    public int Count { get; private set; }
    public int Capacity => _denseComponents.Length;
    public int CapacityGrowthCount { get; private set; }

    public bool HasDirtyRange => _dirtyMax >= _dirtyMin;
    public int DirtyRangeStart => HasDirtyRange ? _dirtyMin : 0;
    public int DirtyRangeEndExclusive => HasDirtyRange ? _dirtyMax + 1 : 0;
    public ReadOnlySpan<RuntimeEntity> Entities => _denseEntities.AsSpan(0, Count);
    public Span<T> Components => _denseComponents.AsSpan(0, Count);
    public HotBitSet DirtyBits => _dirty;

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _denseComponents.Length)
            return;

        int newCapacity = _denseComponents.Length == 0 ? 64 : _denseComponents.Length;
        while (newCapacity < capacity)
            newCapacity *= 2;

        Array.Resize(ref _denseEntities, newCapacity);
        Array.Resize(ref _denseComponents, newCapacity);
        _dirty.EnsureBitCapacity(newCapacity);
        CapacityGrowthCount++;
    }

    public void EnsureEntityCapacity(int entityIndexCapacity)
    {
        if (entityIndexCapacity > _sparse.Length)
            Array.Resize(ref _sparse, entityIndexCapacity);
    }

    public bool Contains(RuntimeEntity entity)
        => TryGetDenseIndex(entity, out _);

    public ref T GetRefAtDenseIndex(int denseIndex)
    {
        if ((uint)denseIndex >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(denseIndex));

        return ref _denseComponents[denseIndex];
    }

    public RuntimeEntity EntityAtDenseIndex(int denseIndex)
    {
        if ((uint)denseIndex >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(denseIndex));

        return _denseEntities[denseIndex];
    }

    public bool TryGet(RuntimeEntity entity, out T component)
    {
        if (TryGetDenseIndex(entity, out int denseIndex))
        {
            component = _denseComponents[denseIndex];
            return true;
        }

        component = default;
        return false;
    }

    public ref T GetOrAdd(RuntimeEntity entity, in T initialValue)
    {
        ValidateAlive(entity);
        if (TryGetDenseIndex(entity, out int existingIndex))
            return ref _denseComponents[existingIndex];

        EnsureEntityCapacity(entity.Index + 1);
        EnsureCapacity(Count + 1);

        int denseIndex = Count++;
        _denseEntities[denseIndex] = entity;
        _denseComponents[denseIndex] = initialValue;
        _sparse[entity.Index] = denseIndex + 1;
        MarkDirtyDense(denseIndex);
        return ref _denseComponents[denseIndex];
    }

    public void Set(RuntimeEntity entity, in T component)
    {
        ref T slot = ref GetOrAdd(entity, component);
        slot = component;
        TryGetDenseIndex(entity, out int denseIndex);
        MarkDirtyDense(denseIndex);
    }

    public bool Remove(RuntimeEntity entity)
        => RemoveEntity(entity);

    public void MarkDirty(RuntimeEntity entity)
    {
        if (!TryGetDenseIndex(entity, out int denseIndex))
            throw new InvalidOperationException("Entity does not have this component.");

        MarkDirtyDense(denseIndex);
    }

    public void ClearDirty()
    {
        _dirty.ClearAll();
        _dirtyMin = int.MaxValue;
        _dirtyMax = -1;
    }

    bool IRuntimeComponentStore.RemoveEntity(RuntimeEntity entity)
        => RemoveEntity(entity);

    private bool RemoveEntity(RuntimeEntity entity)
    {
        if (!TryGetDenseIndex(entity, out int denseIndex))
            return false;

        int lastIndex = Count - 1;
        RuntimeEntity movedEntity = _denseEntities[lastIndex];
        _denseEntities[denseIndex] = movedEntity;
        _denseComponents[denseIndex] = _denseComponents[lastIndex];
        _denseEntities[lastIndex] = default;
        _denseComponents[lastIndex] = default;
        _sparse[entity.Index] = 0;
        if (denseIndex != lastIndex)
        {
            _sparse[movedEntity.Index] = denseIndex + 1;
            MarkDirtyDense(denseIndex);
        }

        Count = lastIndex;
        if (Count > 0)
            MarkDirtyDense(Math.Min(denseIndex, Count - 1));

        return true;
    }

    private bool TryGetDenseIndex(RuntimeEntity entity, out int denseIndex)
    {
        denseIndex = -1;
        if (!_world.IsAlive(entity) || entity.Index >= _sparse.Length)
            return false;

        int sparseValue = _sparse[entity.Index];
        int candidate = sparseValue - 1;
        if ((uint)candidate >= (uint)Count)
            return false;

        RuntimeEntity stored = _denseEntities[candidate];
        if (stored.Index != entity.Index || stored.Generation != entity.Generation)
            return false;

        denseIndex = candidate;
        return true;
    }

    private void ValidateAlive(RuntimeEntity entity)
    {
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException("Runtime entity is not alive.");
    }

    private void MarkDirtyDense(int denseIndex)
    {
        if (denseIndex < 0)
            return;

        _dirty.Set(denseIndex);
        _dirtyMin = Math.Min(_dirtyMin, denseIndex);
        _dirtyMax = Math.Max(_dirtyMax, denseIndex);
    }
}

internal interface IRuntimeComponentStore
{
    bool RemoveEntity(RuntimeEntity entity);
}

public sealed class EntityLookup<TKey>
    where TKey : notnull
{
    private readonly RuntimeEntityWorld _world;
    private readonly Dictionary<TKey, RuntimeEntity> _lookup = [];

    internal EntityLookup(RuntimeEntityWorld world)
    {
        _world = world;
    }

    public int Count => _lookup.Count;

    public void Set(TKey key, RuntimeEntity entity)
    {
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException("Runtime entity is not alive.");

        _lookup[key] = entity;
    }

    public bool TryGet(TKey key, out RuntimeEntity entity)
    {
        if (_lookup.TryGetValue(key, out entity) && _world.IsAlive(entity))
            return true;

        entity = default;
        return false;
    }

    public bool Remove(TKey key)
        => _lookup.Remove(key);
}
