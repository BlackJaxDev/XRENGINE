namespace XREngine;

/// <summary>
/// Lower runtime view of the services an application composition root elected
/// to install. Runtime code uses this contract to reject accidental local-device
/// work in headless profiles without depending on Bootstrap.
/// </summary>
public readonly record struct RuntimeApplicationCapabilities(
    bool IsConfigured,
    bool AllowsLocalInput,
    bool AllowsWindows,
    bool AllowsAudio,
    bool AllowsVr,
    bool AllowsRendererBackends)
{
    public static RuntimeApplicationCapabilities Unconfigured => default;
}

/// <summary>Lease-based publication of the active application capability profile.</summary>
public static class RuntimeApplicationCapabilityServices
{
    private static readonly object Sync = new();
    private static RuntimeApplicationCapabilities _current;

    public static RuntimeApplicationCapabilities Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static IDisposable Install(RuntimeApplicationCapabilities capabilities)
    {
        lock (Sync)
        {
            RuntimeApplicationCapabilities previous = _current;
            _current = capabilities;
            return new InstallationLease(capabilities, previous);
        }
    }

    private sealed class InstallationLease(
        RuntimeApplicationCapabilities installed,
        RuntimeApplicationCapabilities previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (Sync)
            {
                if (_current == installed)
                    _current = previous;
            }
        }
    }
}
