using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal static partial class VulkanCpuSpanProfiler
{
    internal readonly record struct VulkanCpuSpanRecord(
        EVulkanCpuStage Stage,
        long SpanId,
        long ParentSpanId,
        long StartTimestamp,
        long EndTimestamp,
        long AllocatedBytes,
        int ThreadId);
}

