namespace XREngine.RenderBench;

/// <summary>Captured machine state; null means an environmental property was not measured.</summary>
public sealed record RenderBenchEnvironment(
    string OperatingSystem,
    string ProcessPriority,
    string ProfileMode,
    string InstrumentationManifest,
    string ExtensionManifest,
    string? PowerPolicy,
    string? ClockPolicy,
    double? TargetRefreshHertz,
    string? ThermalNotes,
    string? CompetingWorkloadWarnings);
