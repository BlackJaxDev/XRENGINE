namespace XREngine;

/// <summary>
/// Applies a process-local verbosity ceiling while preserving the configured
/// debug host's file routing and recency policy.
/// </summary>
internal sealed class PerformanceProfileDebugHostServices(
    IRuntimeDebugHostServices inner,
    EOutputVerbosity verbosityCeiling)
    : IRuntimeDebugHostServices
{
    public bool LogOutputToFile => inner.LogOutputToFile;

    public EOutputVerbosity OutputVerbosity
        => inner.OutputVerbosity > verbosityCeiling
            ? verbosityCeiling
            : inner.OutputVerbosity;

    public double DebugOutputRecencySeconds
        => inner.DebugOutputRecencySeconds;
}
