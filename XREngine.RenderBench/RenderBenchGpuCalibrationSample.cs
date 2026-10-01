namespace XREngine.RenderBench;

/// <summary>Simultaneous device and Windows QPC ticks with Vulkan's raw maximum deviation.</summary>
public readonly record struct RenderBenchGpuCalibrationSample(
    ulong DeviceTicks,
    ulong QueryPerformanceCounterTicks,
    ulong MaximumDeviationNanoseconds);
