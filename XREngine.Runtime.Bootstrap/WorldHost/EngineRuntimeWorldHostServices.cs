using XREngine.Scene;
using System.Runtime.ExceptionServices;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Installs Bootstrap's explicit world-host registry for Engine-facing Core requests.
/// Registry lifetime is owned by the adapter lease and can be reset deterministically.
/// </summary>
public sealed class EngineRuntimeWorldHostServices : IRuntimeWorldHostServices, IDisposable
{
    private readonly object _sync = new();
    private readonly RuntimeWorldRegistry _coreWorlds = new();
    private readonly Dictionary<XRWorld, RuntimeWorldHost> _hosts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<XRWorld> _worldsBeingRemoved = new(ReferenceEqualityComparer.Instance);
    private bool _resetting;
    private bool _disposed;

    /// <summary>Registry exposed to Core consumers for the lifetime of this host installation.</summary>
    public RuntimeWorldRegistry CoreWorldRegistry => _coreWorlds;

    public RuntimeWorld GetOrCreate(XRWorld world) => GetOrCreateHost(world).CoreWorld;

    public RuntimeWorldHost GetOrCreateHost(XRWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_resetting)
                throw new InvalidOperationException("Runtime world hosts are being reset.");
            if (_worldsBeingRemoved.Contains(world))
                throw new InvalidOperationException("The requested runtime world host is being removed.");
            if (_hosts.TryGetValue(world, out RuntimeWorldHost? existing))
                return existing;

            RuntimeWorldHost created = new(
                RuntimeEngine.Rendering.NewPhysicsScene(),
                RuntimeEngine.Rendering.NewVisualScene());
            _hosts.Add(world, created);
            try
            {
                created.Initialize(
                    world,
                    afterTargetAssigned: () => _coreWorlds.Register(world, created.CoreWorld));
                return created;
            }
            catch
            {
                // Keep the provisional entries visible while teardown callbacks
                // run, then remove every identity published by initialization.
                try
                {
                    created.Dispose();
                }
                finally
                {
                    _coreWorlds.Remove(world, out _, dispose: false);
                    _hosts.Remove(world);
                }
                throw;
            }
        }
    }

    public void Retarget(RuntimeWorld world, XRWorld targetWorld)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(targetWorld);
        ThrowIfDisposed();

        lock (_sync)
        {
            XRWorld previousWorld = world.TargetWorld
                ?? throw new InvalidOperationException("The runtime world has no current XRWorld key.");
            if (ReferenceEquals(previousWorld, targetWorld))
                return;
            if (!_hosts.TryGetValue(previousWorld, out RuntimeWorldHost? host)
                || !ReferenceEquals(host.CoreWorld, world))
            {
                throw new InvalidOperationException("The requested Core world is not owned by this Bootstrap host.");
            }
            if (_hosts.TryGetValue(targetWorld, out RuntimeWorldHost? conflict)
                && !ReferenceEquals(conflict, host))
            {
                throw new InvalidOperationException("The target XRWorld already has a different live runtime host.");
            }

            host.Retarget(
                targetWorld,
                afterTargetAssigned: () =>
                {
                    _hosts.Remove(previousWorld);
                    _hosts.Add(targetWorld, host);
                    _coreWorlds.Retarget(previousWorld, targetWorld, world);
                });
        }
    }

    public bool TryGetHost(XRWorld world, out RuntimeWorldHost? host)
    {
        lock (_sync)
            return _hosts.TryGetValue(world, out host);
    }

    public bool TryGetHost(RuntimeWorld coreWorld, out RuntimeWorldHost? host)
    {
        ArgumentNullException.ThrowIfNull(coreWorld);
        lock (_sync)
        {
            foreach (RuntimeWorldHost candidate in _hosts.Values)
            {
                if (ReferenceEquals(candidate.CoreWorld, coreWorld))
                {
                    host = candidate;
                    return true;
                }
            }
        }

        host = null;
        return false;
    }

    public bool Remove(XRWorld world, bool dispose = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        RuntimeWorldHost host;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_resetting || _worldsBeingRemoved.Contains(world))
                return false;
            if (!_hosts.Remove(world, out host!))
                return false;

            _worldsBeingRemoved.Add(world);
            _coreWorlds.Remove(world, out _, dispose: false);
        }

        try
        {
            if (dispose)
                host.Dispose();
            return true;
        }
        finally
        {
            lock (_sync)
                _worldsBeingRemoved.Remove(world);
        }
    }

    public Task BeginPlayAsync(RuntimeWorld world)
    {
        if (!TryGetHost(world, out RuntimeWorldHost? host) || host is null)
            throw new InvalidOperationException("The requested Core world is not owned by this Bootstrap host.");

        return host.BeginPlayAsync();
    }

    public Task BeginEditModeAsync(RuntimeWorld world)
    {
        if (!TryGetHost(world, out RuntimeWorldHost? host) || host is null)
            throw new InvalidOperationException("The requested Core world is not owned by this Bootstrap host.");

        return host.BeginEditModeAsync();
    }

    public void EndPlay(RuntimeWorld world)
    {
        if (!TryGetHost(world, out RuntimeWorldHost? host) || host is null)
            throw new InvalidOperationException("The requested Core world is not owned by this Bootstrap host.");

        host.EndPlay();
    }

    /// <summary>Disposes all hosts and clears only this installation's registry.</summary>
    public void ResetForTests()
    {
        RuntimeWorldHost[] hosts;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_resetting)
                throw new InvalidOperationException("Runtime world hosts are already being reset.");

            _resetting = true;
            hosts = [.. _hosts.Values];
            _hosts.Clear();
            _coreWorlds.ResetForTests(dispose: false);
        }

        try
        {
            DisposeHosts(hosts);
        }
        finally
        {
            lock (_sync)
                _resetting = false;
        }
    }

    public void Dispose()
    {
        RuntimeWorldHost[] hosts;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _resetting = true;
            hosts = [.. _hosts.Values];
            _hosts.Clear();
            _coreWorlds.ResetForTests(dispose: false);
        }

        DisposeHosts(hosts);
    }

    private static void DisposeHosts(RuntimeWorldHost[] hosts)
    {
        List<Exception>? failures = null;
        foreach (RuntimeWorldHost host in hosts)
        {
            try
            {
                host.Dispose();
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(ex);
            }
        }

        if (failures is [Exception failure])
            ExceptionDispatchInfo.Capture(failure).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException("One or more runtime world hosts failed to dispose.", failures);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
