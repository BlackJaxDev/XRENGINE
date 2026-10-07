namespace XREngine.Execution;

/// <summary>Registers the built-in physical worker domains for native hosts.</summary>
public static class ThreadedWorkerBackend
{
    private static readonly IEngineWorkerDomainFactory BuiltInFactory = new ThreadedWorkerDomainFactory();

    /// <summary>Registers the built-in factory if the process has no factory.</summary>
    public static void EnsureRegistered()
        => EngineWorkerDomainServices.TryRegisterFactory(BuiltInFactory);
}
