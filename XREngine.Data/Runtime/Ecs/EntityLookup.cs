namespace XREngine.Data.Runtime.Ecs;

/// <summary>
/// Provides a lookup mechanism to associate keys of type <typeparamref name="TKey"/> with runtime entities.
/// </summary>
/// <typeparam name="TKey">The type of the key used for the lookup.</typeparam>
public sealed class EntityLookup<TKey>
    where TKey : notnull
{
    private readonly RuntimeEntityWorld _world;
    private readonly Dictionary<TKey, RuntimeEntity> _lookup = [];

    internal EntityLookup(RuntimeEntityWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// Gets the number of key-entity associations currently stored in the lookup.
    /// </summary>
    public int Count => _lookup.Count;

    /// <summary>
    /// Associates the specified key with the given runtime entity. If the entity is not alive, an exception is thrown.
    /// </summary>
    /// <param name="key">The key to associate with the entity.</param>
    /// <param name="entity">The runtime entity to associate with the key.</param>
    /// <exception cref="InvalidOperationException">Thrown if the specified runtime entity is not alive.</exception>
    public void Set(TKey key, RuntimeEntity entity)
    {
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException("Runtime entity is not alive.");

        _lookup[key] = entity;
    }

    /// <summary>
    /// Tries to get the runtime entity associated with the specified key. Returns true if the entity is found and alive; otherwise, false.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="entity">The runtime entity associated with the key, if found and alive; otherwise, default.</param>
    /// <returns>True if the entity is found and alive; otherwise, false.</returns>
    public bool TryGet(TKey key, out RuntimeEntity entity)
    {
        if (_lookup.TryGetValue(key, out entity) && _world.IsAlive(entity))
            return true;

        entity = default;
        return false;
    }

    /// <summary>
    /// Removes the key-entity association for the specified key, if it exists.
    /// </summary>
    /// <param name="key">The key whose association should be removed.</param>
    /// <returns>True if the association was removed; otherwise, false.</returns>
    public bool Remove(TKey key)
        => _lookup.Remove(key);
}
