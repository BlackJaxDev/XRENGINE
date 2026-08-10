using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanMeshBackendServices
{
    private const double OpenXrImageAllocationPressureRatio = 0.84;
    private const long OpenXrImageAllocationPressureReserveBytes = 768L * 1024L * 1024L;
    private const double OpenXrImageAllocationCountPressureRatio = 0.80;
    private const int OpenXrImageAllocationCountReserve = 768;

    /// <summary>
    /// Applies the OpenXR allocation-pressure policy without routing descriptor publication
    /// through the renderer facade. This is a producer-side admission check only; deferred
    /// texture uploads remain owned by the resource runtime.
    /// </summary>
    internal bool ShouldAvoidSynchronousImageAllocationForOpenXr(out string reason)
    {
        reason = string.Empty;
        IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
        if (!presentation.IsOpenXRActive && !presentation.IsInVR)
            return false;

        IVulkanMemoryAllocator? allocator = context.Resources.Allocations.Buffers.MemoryAllocator;
        if (allocator is null)
            return false;

        int activeAllocationCount;
        long allocatedBytes;
        try
        {
            activeAllocationCount = allocator.ActiveVkAllocationCount;
            allocatedBytes = Math.Max(0L, allocator.TotalAllocatedBytes);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
        long trackedBudget = ResolvePressureLimit(frameTiming.TrackedVramBudgetBytes);
        if (trackedBudget != long.MaxValue && Math.Max(0L, frameTiming.TrackedVramBytes) >= trackedBudget)
        {
            reason = $"Vulkan image allocation deferred under tracked VRAM pressure. trackedVram={frameTiming.TrackedVramBytes}, trackedVramDeferLimit={trackedBudget}, activeVkAllocations={activeAllocationCount}";
            return true;
        }

        context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
        ulong largestHeap = 0;
        for (int index = 0; index < memoryProperties.MemoryHeapCount; index++)
            largestHeap = Math.Max(largestHeap, memoryProperties.MemoryHeaps[index].Size);
        long largestHeapBytes = largestHeap > long.MaxValue ? long.MaxValue : (long)largestHeap;
        long allocatorLimit = ResolvePressureLimit(largestHeapBytes);
        if (allocatorLimit != long.MaxValue && allocatedBytes >= allocatorLimit)
        {
            reason = $"Vulkan image allocation deferred under allocator pressure. allocated={allocatedBytes}, largestHeap={largestHeapBytes}, deferLimit={allocatorLimit}, activeVkAllocations={activeAllocationCount}";
            return true;
        }

        context.Api.GetPhysicalDeviceProperties(context.PhysicalDevice, out PhysicalDeviceProperties properties);
        uint maximumAllocationCount = properties.Limits.MaxMemoryAllocationCount;
        if (maximumAllocationCount == 0)
            return false;

        int ratioLimit = (int)Math.Floor(maximumAllocationCount * OpenXrImageAllocationCountPressureRatio);
        int reserveLimit = maximumAllocationCount > OpenXrImageAllocationCountReserve
            ? (int)Math.Min(int.MaxValue, maximumAllocationCount - OpenXrImageAllocationCountReserve)
            : (int)Math.Min(int.MaxValue, maximumAllocationCount);
        int allocationCountLimit = Math.Max(1, Math.Min(ratioLimit, reserveLimit));
        if (activeAllocationCount < allocationCountLimit)
            return false;

        reason = $"Vulkan image allocation deferred under allocation-count pressure. activeVkAllocations={activeAllocationCount}, maxMemoryAllocationCount={maximumAllocationCount}, limit={allocationCountLimit}";
        return true;
    }

    private static long ResolvePressureLimit(long budgetBytes)
    {
        if (budgetBytes <= 0L || budgetBytes == long.MaxValue)
            return long.MaxValue;

        long ratioLimit = (long)Math.Floor(budgetBytes * OpenXrImageAllocationPressureRatio);
        long reserveLimit = budgetBytes > OpenXrImageAllocationPressureReserveBytes
            ? budgetBytes - OpenXrImageAllocationPressureReserveBytes
            : budgetBytes;
        return Math.Max(1L, Math.Min(ratioLimit, reserveLimit));
    }
}
