using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    internal bool TryDescribeCurrentNativeBuffer(
        XRDataBuffer sourceBuffer,
        out VulkanNativeBufferDiagnosticDescription description)
    {
        ArgumentNullException.ThrowIfNull(sourceBuffer);
        description = default;
        if (_resourceRuntime.WrapperLookup.GetOrCreate(sourceBuffer, generateNow: false) is not VkDataBuffer vkBuffer ||
            vkBuffer is not
            {
                IsGenerated: true,
                BufferHandle: { } nativeBuffer,
            } || nativeBuffer.Handle == 0)
        {
            return false;
        }

        VulkanBackendObjectContext context = _resourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
            "The Vulkan backend object context is not initialized.");
        if (!_resourceRuntime.Buffers.TryGetAllocation(nativeBuffer, out VulkanMemoryAllocation allocation))
            return false;

        context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
        uint heapIndex = uint.MaxValue;
        ulong heapSize = 0;
        MemoryHeapFlags heapFlags = 0;
        MemoryPropertyFlags typeFlags = allocation.Properties;
        if (allocation.MemoryTypeIndex < memoryProperties.MemoryTypeCount)
        {
            MemoryType type = memoryProperties.MemoryTypes[(int)allocation.MemoryTypeIndex];
            heapIndex = type.HeapIndex;
            typeFlags = type.PropertyFlags;
            if (heapIndex < memoryProperties.MemoryHeapCount)
            {
                MemoryHeap heap = memoryProperties.MemoryHeaps[(int)heapIndex];
                heapSize = heap.Size;
                heapFlags = heap.Flags;
            }
        }
        description = new VulkanNativeBufferDiagnosticDescription(
            nativeBuffer.Handle,
            vkBuffer.AllocatedByteSize,
            context.GetResourceGeneration(ObjectType.Buffer, nativeBuffer.Handle),
            IsGenerated: true,
            context.IsDeviceOperational,
            allocation.MemoryTypeIndex,
            heapIndex,
            heapSize,
            typeFlags.ToString(),
            heapFlags.ToString(),
            allocation.MappedData != 0,
            allocation.IsCoherent,
            (typeFlags & MemoryPropertyFlags.DeviceLocalBit) != 0,
            allocation.NativeAllocation != 0 ? "VMA" : allocation.BlockId == -1 ? "Dedicated" : "Block");
        return true;
    }
}
