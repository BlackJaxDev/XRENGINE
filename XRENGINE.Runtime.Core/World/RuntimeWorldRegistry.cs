using XREngine.Scene;
using XREngine.Scene.Physics;

namespace XREngine;

/// <summary>
/// Explicit, resettable mapping between world assets and their live Core world
/// contexts. Hosts own registration lifetime and must remove disposed worlds.
/// </summary>
public sealed class RuntimeWorldRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<XRWorld, RuntimeWorld> _worlds = new(ReferenceEqualityComparer.Instance);

    public RuntimeWorld GetOrCreate(XRWorld world, Func<XRWorld, AbstractPhysicsScene> physicsSceneFactory)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(physicsSceneFactory);
        lock (_sync)
        {
            if (_worlds.TryGetValue(world, out RuntimeWorld? existing))
                return existing;

            RuntimeWorld created = new(physicsSceneFactory(world), world);
            _worlds.Add(world, created);
            return created;
        }
    }

    public void Register(XRWorld world, RuntimeWorld runtimeWorld)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(runtimeWorld);
        if (!ReferenceEquals(runtimeWorld.TargetWorld, world))
            throw new ArgumentException("The runtime world must target the registered XRWorld.", nameof(runtimeWorld));
        lock (_sync)
        {
            if (_worlds.TryGetValue(world, out RuntimeWorld? existing))
            {
                if (!ReferenceEquals(existing, runtimeWorld))
                    throw new InvalidOperationException("A different runtime world is already registered for this XRWorld.");
                return;
            }

            _worlds.Add(world, runtimeWorld);
        }
    }

    public bool TryGet(XRWorld world, out RuntimeWorld? runtimeWorld)
    {
        lock (_sync)
            return _worlds.TryGetValue(world, out runtimeWorld);
    }

    /// <summary>Atomically moves an existing runtime identity to a new world-asset key.</summary>
    public void Retarget(XRWorld previousWorld, XRWorld targetWorld, RuntimeWorld runtimeWorld)
    {
        ArgumentNullException.ThrowIfNull(previousWorld);
        ArgumentNullException.ThrowIfNull(targetWorld);
        ArgumentNullException.ThrowIfNull(runtimeWorld);
        if (ReferenceEquals(previousWorld, targetWorld))
            return;

        lock (_sync)
        {
            if (!_worlds.TryGetValue(previousWorld, out RuntimeWorld? existing)
                || !ReferenceEquals(existing, runtimeWorld))
            {
                throw new InvalidOperationException("The runtime world is not registered under its previous XRWorld key.");
            }

            if (_worlds.TryGetValue(targetWorld, out RuntimeWorld? conflict)
                && !ReferenceEquals(conflict, runtimeWorld))
            {
                throw new InvalidOperationException("A different runtime world is already registered for the target XRWorld.");
            }

            _worlds.Remove(previousWorld);
            _worlds[targetWorld] = runtimeWorld;
        }
    }

    public bool Remove(XRWorld world, out RuntimeWorld? runtimeWorld, bool dispose = false)
    {
        bool removed;
        lock (_sync)
            removed = _worlds.Remove(world, out runtimeWorld);
        if (removed && dispose)
            runtimeWorld!.Dispose();
        return removed;
    }

    /// <summary>Returns a stable snapshot suitable for diagnostics and tests.</summary>
    public IReadOnlyDictionary<XRWorld, RuntimeWorld> Snapshot()
    {
        lock (_sync)
            return new Dictionary<XRWorld, RuntimeWorld>(_worlds, ReferenceEqualityComparer.Instance);
    }

    /// <summary>Clears this host registry for deterministic test or host shutdown isolation.</summary>
    public void ResetForTests(bool dispose = true)
    {
        RuntimeWorld[] worlds;
        lock (_sync)
        {
            worlds = [.. _worlds.Values];
            _worlds.Clear();
        }

        if (dispose)
            foreach (RuntimeWorld runtimeWorld in worlds)
                runtimeWorld.Dispose();
    }
}
