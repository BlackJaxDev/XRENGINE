namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Rejects benchmark runs whose process settings would contaminate accepted
/// steady-state evidence.
/// </summary>
public static class PhysicsChainBenchmarkPreflight
{
    public static void Validate(
        PhysicsChainBenchmarkRunPolicy policy,
        PhysicsChainBenchmarkEnvironment environment,
        bool debuggerAttached,
        bool validationLayersEnabled,
        bool verbosePerChainLoggingEnabled,
        bool debugDrawingEnabled,
        bool editorOnlyInstrumentationEnabled)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(environment);
        policy.Validate();

        if (policy.RequireReleaseBuild
            && !string.Equals(environment.BuildConfiguration, "Release", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Accepted physics-chain benchmarks must use a Release build.");
        if (debuggerAttached || validationLayersEnabled || verbosePerChainLoggingEnabled || debugDrawingEnabled || editorOnlyInstrumentationEnabled)
            throw new InvalidOperationException("Accepted physics-chain benchmarks require debugger, validation, verbose per-chain logging, debug drawing, and editor-only instrumentation to be disabled.");
        if (string.IsNullOrWhiteSpace(environment.CpuModel)
            || string.IsNullOrWhiteSpace(environment.GpuModel)
            || string.IsNullOrWhiteSpace(environment.GpuDriver)
            || string.IsNullOrWhiteSpace(environment.WindowsVersion)
            || string.IsNullOrWhiteSpace(environment.PowerMode)
            || environment.PhysicalCoreCount <= 0
            || environment.LogicalCoreCount <= 0
            || environment.MemoryBytes <= 0L
            || environment.Width <= 0
            || environment.Height <= 0
            || environment.RefreshRateHz <= 0)
            throw new InvalidOperationException("Benchmark hardware and presentation metadata must be complete and explicit.");
    }
}
