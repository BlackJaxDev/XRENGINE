namespace XREngine.Rendering.Vulkan;

public static partial class VulkanCpuSpanProfiler
{
    public readonly record struct VulkanCpuSpanRecord(
        EVulkanCpuStage Stage,
        long SpanId,
        long ParentSpanId,
        long StartTimestamp,
        long EndTimestamp,
        long AllocatedBytes,
        int ThreadId,
        long FrameId,
        int WorkerId,
        long InvocationOrdinal,
        string? WaitReason);
}

