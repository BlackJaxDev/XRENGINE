using System.Runtime.ExceptionServices;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Owns Core-only world composition for profiles that forbid rendering and windows.</summary>
internal sealed class HeadlessRuntimeWorldHostServices : IRuntimeWorldHostServices, IDisposable
{
    private readonly object _sync = new();
    private readonly RuntimeWorldRegistry _coreWorlds = new();
    private readonly Dictionary<XRWorld, HeadlessRuntimeWorldHost> _hosts = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public RuntimeWorldRegistry CoreWorldRegistry => _coreWorlds;

    public RuntimeWorld GetOrCreate(XRWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_hosts.TryGetValue(world, out HeadlessRuntimeWorldHost? existing))
                return existing.CoreWorld;

            HeadlessRuntimeWorldHost created = new();
            _hosts.Add(world, created);
            try
            {
                created.Initialize(world, () => _coreWorlds.Register(world, created.CoreWorld));
                return created.CoreWorld;
            }
            catch
            {
                created.Dispose();
                _coreWorlds.Remove(world, out _, dispose: false);
                _hosts.Remove(world);
                throw;
            }
        }
    }

    public void Retarget(RuntimeWorld world, XRWorld targetWorld)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(targetWorld);
        lock (_sync)
        {
            ThrowIfDisposed();
            XRWorld previousWorld = world.TargetWorld
                ?? throw new InvalidOperationException("The runtime world has no current XRWorld key.");
            if (ReferenceEquals(previousWorld, targetWorld))
                return;
            if (!_hosts.TryGetValue(previousWorld, out HeadlessRuntimeWorldHost? host)
                || !ReferenceEquals(host.CoreWorld, world))
                throw new InvalidOperationException("The requested Core world is not owned by this headless host.");
            if (_hosts.ContainsKey(targetWorld))
                throw new InvalidOperationException("The target XRWorld already has a live runtime host.");

            host.Retarget(targetWorld, () =>
            {
                _hosts.Remove(previousWorld);
                _hosts.Add(targetWorld, host);
                _coreWorlds.Retarget(previousWorld, targetWorld, world);
            });
        }
    }

    public bool Remove(XRWorld world, bool dispose = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        HeadlessRuntimeWorldHost? host;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_hosts.Remove(world, out host))
                return false;
            _coreWorlds.Remove(world, out _, dispose: false);
        }

        if (dispose)
            host.Dispose();
        return true;
    }

    public Task BeginPlayAsync(RuntimeWorld world) => Resolve(world).BeginPlayAsync();
    public Task BeginEditModeAsync(RuntimeWorld world) => Resolve(world).BeginEditModeAsync();
    public void EndPlay(RuntimeWorld world) => Resolve(world).EndPlay();

    public void Dispose()
    {
        HeadlessRuntimeWorldHost[] hosts;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            hosts = [.. _hosts.Values];
            _hosts.Clear();
            _coreWorlds.ResetForTests(dispose: false);
        }

        List<Exception>? failures = null;
        foreach (HeadlessRuntimeWorldHost host in hosts)
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
            throw new AggregateException("One or more headless world hosts failed to dispose.", failures);
    }

    private HeadlessRuntimeWorldHost Resolve(RuntimeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (_sync)
        {
            ThrowIfDisposed();
            foreach (HeadlessRuntimeWorldHost host in _hosts.Values)
                if (ReferenceEquals(host.CoreWorld, world))
                    return host;
        }

        throw new InvalidOperationException("The requested Core world is not owned by this headless host.");
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
