namespace XREngine.Rendering.Vulkan;

public static partial class VulkanCpuSpanProfiler
{
    internal readonly record struct VulkanCpuSpanToken(
        ThreadBuffer? Buffer,
        EVulkanCpuStage Stage,
        long Id,
        long ParentSpanId,
        long StartTimestamp,
        long StartAllocatedBytes,
        long FrameId,
        long InvocationOrdinal,
        int WorkerId);
}

