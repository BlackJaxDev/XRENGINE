namespace XREngine.Rendering.Profiling;

/// <summary>Instrumentation explicitly enabled for a run. Non-clean modes are diagnostic only.</summary>
[Flags]
public enum RenderProfileInstrumentation
{
    None = 0,
    AggregateCpu = 1 << 0,
    TargetedCpuSpans = 1 << 1,
    CoarseGpu = 1 << 2,
    TargetedGpuTimestamps = 1 << 3,
    HardwareCounters = 1 << 4,
}
