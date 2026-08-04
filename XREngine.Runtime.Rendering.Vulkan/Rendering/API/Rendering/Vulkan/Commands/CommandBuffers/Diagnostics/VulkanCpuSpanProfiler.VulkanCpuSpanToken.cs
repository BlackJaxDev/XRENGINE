using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal static partial class VulkanCpuSpanProfiler
{
    internal readonly record struct VulkanCpuSpanToken(
        ThreadBuffer? Buffer,
        EVulkanCpuStage Stage,
        long Id,
        long ParentSpanId,
        long StartTimestamp,
        long StartAllocatedBytes);
}

