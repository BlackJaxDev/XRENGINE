namespace XREngine;

/// <summary>Explicit installation point for the Bootstrap world host.</summary>
public static class RuntimeWorldHostServices
{
    private static readonly Lock Sync = new();
    private static IRuntimeWorldHostServices? _current;

    public static IRuntimeWorldHostServices? Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static IDisposable Install(IRuntimeWorldHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (Sync)
        {
            IRuntimeWorldHostServices? previous = _current;
            _current = host;
            return new InstallationLease(host, previous);
        }
    }

    private sealed class InstallationLease(IRuntimeWorldHostServices installed, IRuntimeWorldHostServices? previous) : IDisposable
    {
        private IRuntimeWorldHostServices? _installed = installed;
        private readonly IRuntimeWorldHostServices? _previous = previous;

        public void Dispose()
        {
            IRuntimeWorldHostServices? installed = Interlocked.Exchange(ref _installed, null);
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
