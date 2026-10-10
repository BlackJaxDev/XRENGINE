namespace XREngine.Execution;

/// <summary>Holds the process worker-domain factory.</summary>
public static class EngineWorkerDomainServices
{
    private static IEngineWorkerDomainFactory? _factory;

    /// <summary>Gets the registered factory without creating worker domains.</summary>
    public static IEngineWorkerDomainFactory? Factory => Volatile.Read(ref _factory);

    /// <summary>Installs a factory only when no factory is registered.</summary>
    public static bool TryRegisterFactory(IEngineWorkerDomainFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Interlocked.CompareExchange(ref _factory, factory, null) is null;
    }

    /// <summary>Gets the factory required by native worker execution.</summary>
    public static IEngineWorkerDomainFactory GetRequiredFactory()
        => Factory ?? throw new InvalidOperationException(
            "No engine worker-domain factory is registered. Call ThreadedWorkerBackend.EnsureRegistered() before creating threaded jobs.");
}
