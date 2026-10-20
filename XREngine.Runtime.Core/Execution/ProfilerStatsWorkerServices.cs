namespace XREngine.Execution;

/// <summary>Holds the process factory for profiler statistics workers.</summary>
internal static class ProfilerStatsWorkerServices
{
    private static IProfilerStatsWorkerFactory? _factory;

    /// <summary>Gets the registered factory without creating a worker.</summary>
    internal static IProfilerStatsWorkerFactory? Factory => Volatile.Read(ref _factory);

    /// <summary>Installs a factory only when the slot is empty.</summary>
    internal static bool TryRegisterFactory(IProfilerStatsWorkerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Interlocked.CompareExchange(ref _factory, factory, null) is null;
    }

    /// <summary>Gets the factory required for native profiler statistics.</summary>
    internal static IProfilerStatsWorkerFactory GetRequiredFactory()
        => Factory ?? throw new InvalidOperationException(
            "Execution.ProfilerWorkerUnavailable: Register a profiler statistics worker factory before the first native DEBUG Engine access, or before enabling frame logging in Release.");
}
