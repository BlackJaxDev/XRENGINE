namespace XREngine.Data.Runtime.Ecs;

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
