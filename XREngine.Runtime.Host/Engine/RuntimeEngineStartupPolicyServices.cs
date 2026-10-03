namespace XREngine;

/// <summary>Installs the startup policy independently of engine static initialization.</summary>
public static class RuntimeEngineStartupPolicyServices
{
    private static readonly Lock Sync = new();
    private static IRuntimeEngineStartupPolicy? _current;

    /// <summary>Requires an explicit startup policy from the application's composition root.</summary>
    public static IRuntimeEngineStartupPolicy Require()
    {
        lock (Sync)
            return _current ?? throw new InvalidOperationException(
                "No IRuntimeEngineStartupPolicy is installed. Install the application host before initializing the engine or generating default startup settings.");
    }

    /// <summary>Installs a policy and returns a lease restoring the previous policy.</summary>
    public static IDisposable Install(IRuntimeEngineStartupPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (Sync)
        {
            IRuntimeEngineStartupPolicy? previous = _current;
            _current = policy;
            return new InstallationLease(policy, previous);
        }
    }

    private sealed class InstallationLease(IRuntimeEngineStartupPolicy installed, IRuntimeEngineStartupPolicy? previous) : IDisposable
    {
        private IRuntimeEngineStartupPolicy? _installed = installed;

        public void Dispose()
        {
            IRuntimeEngineStartupPolicy? installed = Interlocked.Exchange(ref _installed, null);
            if (installed is null)
                return;
            lock (Sync)
                if (ReferenceEquals(_current, installed))
                    _current = previous;
        }
    }
}
