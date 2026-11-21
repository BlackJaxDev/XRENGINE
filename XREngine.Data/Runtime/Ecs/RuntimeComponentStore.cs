using XREngine.Data.Runtime.Collections;

namespace XREngine.Data.Runtime.Ecs;

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
