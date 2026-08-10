using System;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns allocator setup and allocation-pressure decisions for one Vulkan resource generation.</summary>
internal sealed unsafe partial class VulkanResourceRuntime
{
    private const double ResourceRuntimeImageAllocationPressurePreflightRatio = 0.84;
    private const long ResourceRuntimeImageAllocationPressureReserveBytes = 768L * 1024L * 1024L;
    private const double ResourceRuntimeImageAllocationCountPreflightRatio = 0.80;
    private const int ResourceRuntimeImageAllocationCountReserve = 768;

    internal void InitializeMemoryAllocator(
        Vk api,
        VulkanDeviceContext deviceContext,
        EVulkanAllocatorBackend backend,
        bool supportsBufferDeviceAddress)
    {
        api.GetPhysicalDeviceMemoryProperties(deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
        for (int index = 0; index < memoryProperties.MemoryTypeCount; index++)
        {
            if (!memoryProperties.MemoryTypes[index].PropertyFlags.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit))
                continue;

            deviceContext.MutableCapabilities.SupportsLazyAllocation = true;
            break;
        }

        Allocations.Buffers.MemoryAllocator = backend switch
        {
            EVulkanAllocatorBackend.Legacy => new VulkanLegacyAllocator(deviceContext),
            EVulkanAllocatorBackend.Managed => new VulkanBlockAllocator(deviceContext),
            EVulkanAllocatorBackend.Vma => new VulkanVmaAllocator(
                deviceContext.Instance,
                deviceContext.PhysicalDevice,
                deviceContext.Device,
                Vk.Version13,
                supportsBufferDeviceAddress),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown Vulkan allocator backend.")
        };
        Debug.Vulkan($"[Vulkan] Memory allocator initialized: {backend} (lazyAlloc={deviceContext.MutableCapabilities.SupportsLazyAllocation})");
    }

    internal bool TryMapMemoryAllocation(Vk api, VulkanDeviceContext deviceContext, VulkanMemoryAllocation allocation, ulong offset, ulong length, out void* mapped)
    {
        bool mappedSuccessfully = RequireMemoryAllocator().TryMap(
            api,
            deviceContext.Device,
            allocation,
            offset,
            length,
            out mapped,
            out Result result);
        if (!mappedSuccessfully)
            deviceContext.ObserveNativeResult("vkMapMemory.Allocator", result);
        return mappedSuccessfully;
    }

    internal void UnmapMemoryAllocation(Vk api, Device device, VulkanMemoryAllocation allocation)
        => RequireMemoryAllocator().Unmap(api, device, allocation);

    internal VulkanMemoryAllocation AllocateBufferMemoryWithFallback(
        Vk api, VulkanDeviceContext deviceContext, Buffer buffer, MemoryPropertyFlags requiredProperties)
    {
        IVulkanMemoryAllocator allocator = RequireMemoryAllocator();
        if (allocator.TryAllocateForBuffer(api, deviceContext.Device, buffer, requiredProperties, out VulkanMemoryAllocation allocation, out Result result))
            return allocation;

        deviceContext.ObserveNativeResult("vkAllocateMemory.TargetBuffer", result);
        if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit) &&
            allocator.TryAllocateForBuffer(api, deviceContext.Device, buffer, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out allocation, out result))
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();
            return allocation;
        }

        deviceContext.ObserveNativeResult("vkAllocateMemory.TargetBufferFallback", result);
        throw new VulkanOutOfMemoryException($"Vulkan target buffer allocation failed ({result}). Requested={requiredProperties}", requiredProperties);
    }

    internal VulkanMemoryAllocation AllocateImageMemoryWithFallback(
        Vk api, VulkanDeviceContext deviceContext, Image image, MemoryPropertyFlags requiredProperties)
    {
        IVulkanMemoryAllocator allocator = RequireMemoryAllocator();
        if (allocator.TryAllocateForImage(api, deviceContext.Device, image, requiredProperties, out VulkanMemoryAllocation allocation, out Result result))
            return allocation;

        deviceContext.ObserveNativeResult("vkAllocateMemory.TargetImage", result);
        if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit) &&
            allocator.TryAllocateForImage(api, deviceContext.Device, image, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out allocation, out result))
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();
            return allocation;
        }

        deviceContext.ObserveNativeResult("vkAllocateMemory.TargetImageFallback", result);
        throw new VulkanOutOfMemoryException($"Vulkan target image allocation failed ({result}). Requested={requiredProperties}", requiredProperties);
    }

    internal bool TryAllocateImageMemoryWithFallback(
        Vk api,
        VulkanDeviceContext deviceContext,
        Image image,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation,
        out string failureReason)
    {
        try
        {
            allocation = AllocateImageMemoryWithFallback(api, deviceContext, image, requiredProperties);
            failureReason = string.Empty;
            return true;
        }
        catch (VulkanOutOfMemoryException exception)
        {
            allocation = default;
            failureReason = exception.Message;
            return false;
        }
    }

    internal void FreeMemoryAllocation(Vk api, Device device, VulkanMemoryAllocation allocation)
    {
        if (allocation.Memory.Handle != 0)
            RequireMemoryAllocator().Free(api, device, allocation);
    }

    internal bool ShouldDeferImageMemoryAllocationForPressure(
        Vk api,
        VulkanDeviceContext deviceContext,
        Image image,
        MemoryPropertyFlags requiredProperties,
        IRuntimeRenderPresentationServices presentation,
        IRuntimeRenderFrameTimingServices frameTiming,
        out string reason)
    {
        reason = string.Empty;
        if (!requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit) ||
            deviceContext.Device.Handle == 0 || image.Handle == 0 ||
            (!presentation.IsOpenXRActive && !presentation.IsInVR))
            return false;

        api.GetImageMemoryRequirements(deviceContext.Device, image, out MemoryRequirements requirements);
        long requestedBytes = requirements.Size > long.MaxValue ? long.MaxValue : (long)requirements.Size;
        return TryDescribeOpenXrImageAllocationPressure(
            api, deviceContext, presentation, frameTiming, requestedBytes, requiredProperties, out reason);
    }

    internal bool ShouldAvoidSynchronousImageAllocationForOpenXr(
        Vk api,
        VulkanDeviceContext deviceContext,
        IRuntimeRenderPresentationServices presentation,
        IRuntimeRenderFrameTimingServices frameTiming,
        out string reason)
        => presentation.IsOpenXRActive || presentation.IsInVR
            ? TryDescribeOpenXrImageAllocationPressure(
                api, deviceContext, presentation, frameTiming, 0L, MemoryPropertyFlags.DeviceLocalBit, out reason)
            : SetNoPressure(out reason);

    internal bool TryGetAllocatorBudgetSnapshot(
        Vk api,
        VulkanDeviceContext deviceContext,
        double budgetRatio,
        long reserveBytes,
        out long allocatedBytes,
        out long budgetBytes,
        out long largestHeapBytes,
        out int activeAllocationCount)
    {
        allocatedBytes = budgetBytes = largestHeapBytes = 0L;
        activeAllocationCount = 0;
        IVulkanMemoryAllocator allocator;
        try { allocator = RequireMemoryAllocator(); activeAllocationCount = allocator.ActiveVkAllocationCount; }
        catch (InvalidOperationException) { return false; }

        if (allocator is VulkanVmaAllocator vmaAllocator && deviceContext.PhysicalDevice.Handle != 0)
        {
            api.GetPhysicalDeviceMemoryProperties(deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties properties);
            if (vmaAllocator.TryGetDeviceLocalHeapBudgetSnapshot(in properties, budgetRatio, reserveBytes, out allocatedBytes, out budgetBytes, out largestHeapBytes))
                return true;
        }

        try { allocatedBytes = allocator.TotalAllocatedBytes; }
        catch (InvalidOperationException) { return false; }
        largestHeapBytes = ResolveLargestVulkanMemoryHeapBytes(api, deviceContext);
        if (largestHeapBytes <= 0)
            return false;
        double clampedRatio = Math.Clamp(budgetRatio, 0.1, 1.0);
        long ratioLimitBytes = (long)Math.Floor(largestHeapBytes * clampedRatio);
        long reserveLimitBytes = largestHeapBytes > reserveBytes ? largestHeapBytes - Math.Max(0L, reserveBytes) : largestHeapBytes;
        budgetBytes = Math.Max(0L, Math.Min(ratioLimitBytes, reserveLimitBytes));
        return budgetBytes > 0L;
    }

    private bool TryDescribeOpenXrImageAllocationPressure(
        Vk api, VulkanDeviceContext deviceContext, IRuntimeRenderPresentationServices presentation,
        IRuntimeRenderFrameTimingServices frameTiming, long requestedBytes, MemoryPropertyFlags requiredProperties, out string reason)
    {
        reason = string.Empty;
        if (!presentation.IsOpenXRActive && !presentation.IsInVR)
            return false;
        if (!TryGetAllocatorBudgetSnapshot(api, deviceContext, ResourceRuntimeImageAllocationPressurePreflightRatio, ResourceRuntimeImageAllocationPressureReserveBytes, out long allocated, out long limit, out long largestHeap, out int activeCount))
            return false;
        long tracked = Math.Max(0L, frameTiming.TrackedVramBytes);
        long trackedLimit = ResolveTrackedVramLimit(frameTiming.TrackedVramBudgetBytes);
        long projectedAllocator = allocated > long.MaxValue - requestedBytes ? long.MaxValue : allocated + requestedBytes;
        if (projectedAllocator >= limit)
        {
            reason = $"Vulkan image allocation deferred under allocator pressure. requested={requestedBytes}, allocated={allocated}, projectedAllocated={projectedAllocator}, largestHeap={largestHeap}, deferLimit={limit}, activeVkAllocations={activeCount}, requestedProperties={requiredProperties}";
            return true;
        }
        long projectedTracked = tracked > long.MaxValue - requestedBytes ? long.MaxValue : tracked + requestedBytes;
        if (trackedLimit != long.MaxValue && projectedTracked >= trackedLimit)
        {
            reason = $"Vulkan image allocation deferred under tracked VRAM pressure. requested={requestedBytes}, trackedVram={tracked}, projectedTrackedVram={projectedTracked}, trackedVramDeferLimit={trackedLimit}, activeVkAllocations={activeCount}, requestedProperties={requiredProperties}";
            return true;
        }
        api.GetPhysicalDeviceProperties(deviceContext.PhysicalDevice, out PhysicalDeviceProperties properties);
        uint maximum = properties.Limits.MaxMemoryAllocationCount;
        int countLimit = Math.Max(1, Math.Min((int)Math.Floor(maximum * ResourceRuntimeImageAllocationCountPreflightRatio), maximum > ResourceRuntimeImageAllocationCountReserve ? (int)Math.Min(int.MaxValue, maximum - ResourceRuntimeImageAllocationCountReserve) : (int)Math.Min(int.MaxValue, maximum)));
        if (maximum == 0 || activeCount < countLimit)
            return false;
        reason = $"Vulkan image allocation deferred under allocation-count pressure. activeVkAllocations={activeCount}, maxMemoryAllocationCount={maximum}, limit={countLimit}, requested={requestedBytes}, requestedProperties={requiredProperties}";
        return true;
    }

    private static bool SetNoPressure(out string reason) { reason = string.Empty; return false; }
    private static long ResolveTrackedVramLimit(long budget) => budget <= 0L || budget == long.MaxValue ? long.MaxValue : Math.Max(1L, Math.Min((long)Math.Floor(budget * ResourceRuntimeImageAllocationPressurePreflightRatio), budget > ResourceRuntimeImageAllocationPressureReserveBytes ? budget - ResourceRuntimeImageAllocationPressureReserveBytes : budget));
    private static long ResolveLargestVulkanMemoryHeapBytes(Vk api, VulkanDeviceContext deviceContext)
    {
        if (deviceContext.PhysicalDevice.Handle == 0) return 0L;
        api.GetPhysicalDeviceMemoryProperties(deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties properties);
        ulong largest = 0UL;
        for (int index = 0; index < properties.MemoryHeapCount; index++) largest = Math.Max(largest, properties.MemoryHeaps[index].Size);
        return largest > long.MaxValue ? long.MaxValue : (long)largest;
    }
    private IVulkanMemoryAllocator RequireMemoryAllocator() => Allocations.Buffers.MemoryAllocator ?? throw new InvalidOperationException("The Vulkan memory allocator is not initialized.");

    internal static void* Allocated(void* userData, nuint size, nuint alignment, SystemAllocationScope allocationScope)
        => null;

    internal static void* Reallocated(void* userData, void* original, nuint size, nuint alignment, SystemAllocationScope allocationScope)
        => null;

    internal static void Freed(void* userData, void* memory) { }

    internal static void InternalAllocated(void* userData, nuint size, InternalAllocationType allocationType, SystemAllocationScope allocationScope) { }

    internal static void InternalFreed(void* userData, nuint size, InternalAllocationType allocationType, SystemAllocationScope allocationScope) { }
}
