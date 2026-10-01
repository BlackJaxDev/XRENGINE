namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Explicit installation point for optional world-host composition such as
/// editor-only scene policy. Runtime applications may leave it uninstalled.
/// </summary>
public static class RuntimeWorldHostCompositionServices
{
    private static IRuntimeWorldHostCompositionServices? _current;

    public static IDisposable Install(IRuntimeWorldHostCompositionServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IRuntimeWorldHostCompositionServices? previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    internal static void Compose(RuntimeWorldHost host)
        => Volatile.Read(ref _current)?.Compose(host);

    private sealed class InstallationLease(
        IRuntimeWorldHostCompositionServices installed,
        IRuntimeWorldHostCompositionServices? previous) : IDisposable
    {
        private IRuntimeWorldHostCompositionServices? _installed = installed;

        public void Dispose()
        {
            IRuntimeWorldHostCompositionServices? installed = Interlocked.Exchange(ref _installed, null);
            if (installed is not null)
                Interlocked.CompareExchange(ref _current, previous, installed);
        }
    }
}
