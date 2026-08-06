using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>One allocation-free CPU-stage aggregate emitted with a settled telemetry root.</summary>
public readonly record struct VulkanCpuStageTelemetry(
    EVulkanCpuStage Stage,
    TimeSpan Elapsed,
    long AllocatedBytes,
    long AllocationHighWaterBytes,
    long BoundaryAllocatedBytes,
    long BoundaryAllocationHighWaterBytes,
    long InvocationCount,
    TimeSpan CumulativeElapsed,
    TimeSpan PeakElapsed);
