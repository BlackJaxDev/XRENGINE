namespace XREngine;

/// <summary>Explicit installation point for the registry owned by the active runtime host.</summary>
public static class RuntimeWorldRegistryServices
{
    private static readonly Lock Sync = new();
    private static RuntimeWorldRegistry? _current;

    public static RuntimeWorldRegistry? Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static IDisposable Install(RuntimeWorldRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        lock (Sync)
        {
            RuntimeWorldRegistry? previous = _current;
            _current = registry;
            return new InstallationLease(registry, previous);
        }
    }

    private sealed class InstallationLease(RuntimeWorldRegistry installed, RuntimeWorldRegistry? previous) : IDisposable
    {
        private RuntimeWorldRegistry? _installed = installed;
        private readonly RuntimeWorldRegistry? _previous = previous;

        public void Dispose()
        {
            RuntimeWorldRegistry? installed = Interlocked.Exchange(ref _installed, null);
            if (installed is null)
                return;

            lock (Sync)
            {
                if (ReferenceEquals(_current, installed))
                    _current = _previous;
            }
        }
    }
}
