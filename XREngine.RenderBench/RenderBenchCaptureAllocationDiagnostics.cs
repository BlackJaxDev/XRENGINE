using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Per-frame capture-thread allocation attribution emitted before a profile
/// gate is evaluated. The arrays are preallocated before capture and copied
/// only after the measured interval has ended.
/// </summary>
internal sealed record RenderBenchCaptureAllocationDiagnostics(
    long CaptureThreadAllocatedBytes,
    long[] FixtureRecordAllocatedBytes,
    long[] SubmitFrameAllocatedBytes,
    long[] DelayedGpuTimingAllocatedBytes,
    VulkanExplicitTargetFrameAllocationCounters[] ExplicitTargetFrames);
