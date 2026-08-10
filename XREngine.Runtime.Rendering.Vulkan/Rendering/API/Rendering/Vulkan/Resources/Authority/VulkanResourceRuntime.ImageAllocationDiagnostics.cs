using System.Linq;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    internal void TrackImageAllocation(
        VulkanDeviceContext deviceContext,
        Image image,
        VulkanMemoryAllocation allocation,
        string? name,
        string source,
        uint width,
        uint height,
        uint depth,
        uint layers,
        uint mipLevels,
        Format format,
        ImageUsageFlags usage,
        SampleCountFlags samples)
    {
        if (image.Handle == 0)
            return;

        string resolvedName = string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name!;
        RegisterResource(ObjectType.Image, image.Handle, $"{source}:{resolvedName}");
        ResolveImageAllocationDiagnosticFields(
            deviceContext,
            allocation,
            out uint heapIndex,
            out ulong heapSize,
            out MemoryHeapFlags heapFlags,
            out MemoryPropertyFlags memoryTypeFlags);
        string allocationClass = ClassifyVulkanAllocation(
            allocation.Properties,
            allocation.Properties);
        Allocations.Images.DebugInfo[image.Handle] = new VulkanImageAllocationDebugInfo(
            image.Handle,
            resolvedName,
            source,
            allocation.Size > long.MaxValue ? long.MaxValue : (long)allocation.Size,
            width,
            height,
            depth,
            layers,
            Math.Max(1u, mipLevels),
            format.ToString(),
            usage.ToString(),
            samples.ToString(),
            allocationClass,
            allocation.MemoryTypeIndex,
            memoryTypeFlags.ToString(),
            heapIndex,
            heapSize,
            heapFlags.ToString());

        RecordImageAllocationDiagnostics(
            image,
            allocation,
            resolvedName,
            source,
            width,
            height,
            depth,
            layers,
            mipLevels,
            format,
            usage,
            samples,
            allocationClass,
            heapIndex,
            heapSize,
            heapFlags,
            memoryTypeFlags);
    }

    internal void UntrackImageAllocation(Image image)
    {
        if (image.Handle != 0)
            Allocations.Images.DebugInfo.TryRemove(image.Handle, out _);
    }

    internal object GetLiveImageAllocationDiagnostics(int limit)
    {
        int clampedLimit = Math.Clamp(limit, 1, 512);
        VulkanImageAllocationDebugInfo[] entries =
            Allocations.Images.DebugInfo.Values.ToArray();
        long knownBytes = 0L;
        for (int index = 0; index < entries.Length; index++)
        {
            long size = entries[index].SizeBytes;
            knownBytes = knownBytes > long.MaxValue - size
                ? long.MaxValue
                : knownBytes + size;
        }

        object[] largest = entries
            .OrderByDescending(static entry => entry.SizeBytes)
            .Take(clampedLimit)
            .Select(static entry => (object)new
            {
                handle = $"0x{entry.Handle:X}",
                entry.Name,
                entry.Source,
                entry.SizeBytes,
                sizeMiB = entry.SizeBytes / (1024.0 * 1024.0),
                entry.Width,
                entry.Height,
                entry.Depth,
                entry.Layers,
                entry.MipLevels,
                entry.Format,
                entry.Usage,
                entry.Samples,
                entry.AllocationClass,
                entry.MemoryTypeIndex,
                entry.MemoryTypeFlags,
                entry.MemoryHeapIndex,
                entry.MemoryHeapSize,
                entry.MemoryHeapFlags,
            })
            .ToArray();
        IVulkanMemoryAllocator? allocator = Allocations.Buffers.MemoryAllocator;
        return new
        {
            allocatorActiveVkAllocations = allocator?.ActiveVkAllocationCount ?? 0,
            allocatorTotalAllocatedBytes = allocator?.TotalAllocatedBytes ?? 0L,
            knownImageAllocationCount = entries.Length,
            knownImageAllocationBytes = knownBytes,
            knownImageAllocationMiB = knownBytes / (1024.0 * 1024.0),
            largest,
        };
    }

    private static void ResolveImageAllocationDiagnosticFields(
        VulkanDeviceContext deviceContext,
        VulkanMemoryAllocation allocation,
        out uint heapIndex,
        out ulong heapSize,
        out MemoryHeapFlags heapFlags,
        out MemoryPropertyFlags memoryTypeFlags)
    {
        heapIndex = uint.MaxValue;
        heapSize = 0UL;
        heapFlags = 0;
        memoryTypeFlags = 0;
        if (deviceContext.PhysicalDevice.Handle == 0)
            return;

        deviceContext.Api.GetPhysicalDeviceMemoryProperties(
            deviceContext.PhysicalDevice,
            out PhysicalDeviceMemoryProperties memoryProperties);
        if (allocation.MemoryTypeIndex >= memoryProperties.MemoryTypeCount)
            return;

        MemoryType memoryType = memoryProperties.MemoryTypes[(int)allocation.MemoryTypeIndex];
        heapIndex = memoryType.HeapIndex;
        memoryTypeFlags = memoryType.PropertyFlags;
        if (heapIndex >= memoryProperties.MemoryHeapCount)
            return;

        MemoryHeap heap = memoryProperties.MemoryHeaps[(int)heapIndex];
        heapSize = heap.Size;
        heapFlags = heap.Flags;
    }

    private void RecordImageAllocationDiagnostics(
        Image image,
        VulkanMemoryAllocation allocation,
        string name,
        string source,
        uint width,
        uint height,
        uint depth,
        uint layers,
        uint mipLevels,
        Format format,
        ImageUsageFlags usage,
        SampleCountFlags samples,
        string allocationClass,
        uint heapIndex,
        ulong heapSize,
        MemoryHeapFlags heapFlags,
        MemoryPropertyFlags memoryTypeFlags)
    {
        if (!RenderDiagnosticsFlags.UploadStageLogging &&
            !RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
        {
            return;
        }

        IVulkanMemoryAllocator? allocator = Allocations.Buffers.MemoryAllocator;
        Debug.Vulkan(
            "[VkImageAllocation] image=0x{0:X} name='{1}' source={2} allocationClass={3} memoryHeap={4} heapSize={5} heapFlags={6} memoryType={7} memoryTypeFlags={8} size={9} extent={10}x{11}x{12} layers={13} mips={14} format={15} usage={16} samples={17} activeVkAllocations={18} allocatorBytes={19}.",
            image.Handle,
            name,
            source,
            allocationClass,
            heapIndex,
            heapSize,
            heapFlags,
            allocation.MemoryTypeIndex,
            memoryTypeFlags,
            allocation.Size,
            width,
            height,
            depth,
            layers,
            Math.Max(1u, mipLevels),
            format,
            usage,
            samples,
            allocator?.ActiveVkAllocationCount ?? 0,
            allocator?.TotalAllocatedBytes ?? 0L);
    }

    private static string ClassifyVulkanAllocation(
        MemoryPropertyFlags requestedProperties,
        MemoryPropertyFlags allocationProperties)
    {
        bool deviceLocal = (allocationProperties & MemoryPropertyFlags.DeviceLocalBit) != 0;
        bool hostVisible = (allocationProperties & MemoryPropertyFlags.HostVisibleBit) != 0;
        bool hostCached = (allocationProperties & MemoryPropertyFlags.HostCachedBit) != 0;
        if (deviceLocal && hostVisible)
            return "DeviceLocalHostVisible";
        if (deviceLocal)
            return "DeviceLocal";
        if (hostVisible && hostCached)
            return "Readback";
        if (hostVisible)
            return (requestedProperties & MemoryPropertyFlags.DeviceLocalBit) != 0
                ? "UploadFallback"
                : "Upload";
        return "Other";
    }
}
