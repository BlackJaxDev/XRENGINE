using System;
using System.Threading;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Legacy allocator that wraps per-resource <c>vkAllocateMemory</c> calls.
/// Each buffer/image receives its own dedicated <see cref="DeviceMemory"/>.
/// This is the baseline. The block suballocator provides the modern path.
/// </summary>
internal sealed unsafe class VulkanLegacyAllocator : IVulkanMemoryAllocator
{
    private readonly VulkanRenderer _renderer;
    private int _activeVkAllocationCount;
    private long _totalAllocatedBytes;

    public VulkanLegacyAllocator(VulkanRenderer renderer)
    {
        _renderer = renderer;
    }

    public int ActiveVkAllocationCount => _activeVkAllocationCount;
    public long TotalAllocatedBytes => _totalAllocatedBytes;

    public VulkanMemoryAllocation AllocateForBuffer(
        Vk api, Device device, Buffer buffer, MemoryPropertyFlags requiredProperties)
    {
        if (!TryAllocateForBuffer(api, device, buffer, requiredProperties, out VulkanMemoryAllocation allocation))
            throw new VulkanOutOfMemoryException("Failed to allocate Vulkan buffer memory.", requiredProperties);
        return allocation;
    }

    public VulkanMemoryAllocation AllocateForImage(
        Vk api, Device device, Image image, MemoryPropertyFlags requiredProperties)
    {
        if (!TryAllocateForImage(api, device, image, requiredProperties, out VulkanMemoryAllocation allocation))
            throw new VulkanOutOfMemoryException("Failed to allocate Vulkan image memory.", requiredProperties);
        return allocation;
    }

    public bool TryAllocateForBuffer(
        Vk api, Device device, Buffer buffer,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation)
    {
        api.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements memReqs);
        return TryAllocateCore(api, device, memReqs, requiredProperties, out allocation);
    }

    public bool TryAllocateForImage(
        Vk api, Device device, Image image,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation)
    {
        api.GetImageMemoryRequirements(device, image, out MemoryRequirements memReqs);
        return TryAllocateCore(api, device, memReqs, requiredProperties, out allocation);
    }

    private bool TryAllocateCore(
        Vk api, Device device,
        MemoryRequirements memReqs,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation)
    {
        allocation = VulkanMemoryAllocation.Null;

        uint memoryTypeIndex = _renderer.ResolveMemoryType(memReqs.MemoryTypeBits, requiredProperties);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = memoryTypeIndex,
        };

        Result result = api.AllocateMemory(device, ref allocInfo, null, out DeviceMemory memory);

        if (result == Result.ErrorOutOfDeviceMemory || result == Result.ErrorOutOfHostMemory)
            return false;

        if (result != Result.Success)
            return false;

        Interlocked.Increment(ref _activeVkAllocationCount);
        Interlocked.Add(ref _totalAllocatedBytes, (long)memReqs.Size);

        allocation = new VulkanMemoryAllocation(
            Memory: memory,
            Offset: 0,
            Size: memReqs.Size,
            MemoryTypeIndex: memoryTypeIndex,
            Properties: requiredProperties,
            BlockId: -1);

        return true;
    }

    public void Free(Vk api, Device device, VulkanMemoryAllocation allocation)
    {
        if (allocation.IsNull)
            return;

        api.FreeMemory(device, allocation.Memory, null);
        Interlocked.Decrement(ref _activeVkAllocationCount);
        Interlocked.Add(ref _totalAllocatedBytes, -(long)allocation.Size);
    }

    public void Dispose()
    {
        // Legacy allocator doesn't own any pooled blocks — individual callers
        // free their own DeviceMemory handles during normal teardown.
    }
}
