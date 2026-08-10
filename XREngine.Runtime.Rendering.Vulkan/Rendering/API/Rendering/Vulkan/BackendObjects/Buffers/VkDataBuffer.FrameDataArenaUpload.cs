using XREngine.Data;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkDataBuffer
{
    /// <summary>
    /// Copies one CPU data range through the canonical persistently mapped frame-slot
    /// arena. The synchronous GPU copy proves source completion before this method
    /// returns, while the arena retains its buffer and mapping for later frames.
    /// </summary>
    private bool UploadDeviceLocalRangeFromFrameDataArena(
        VoidPtr source,
        uint length,
        Buffer destination,
        ulong destinationOffset,
        string owner)
    {
        if (!BackendContext.Resources.TryAcquireSynchronousFrameDataArenaLease(
                out VulkanSynchronousFrameDataArenaLease lease))
            throw new InvalidOperationException(
                "The Vulkan synchronous frame-data arena is unavailable for a device-local upload.");
        using VulkanSynchronousFrameDataArenaLease ownedLease = lease;
        {
            VulkanFrameDataArena arena = ownedLease.Arena;
            const int frameSlot = 0;
            EVulkanFrameDataLane lane = ResolveFrameDataLane(_lastUsageFlags);
            if (!arena.TryAllocateWrite(
                    frameSlot,
                    lane,
                    source.Pointer,
                    sourceOffset: 0,
                    length,
                    alignment: 16,
                    out VulkanFrameDataSlice slice))
            {
                throw new InvalidOperationException(
                    $"Vulkan frame-data arena rejected {length:N0} {lane} bytes in slot {frameSlot}.");
            }

            BackendContext.Resources.SynchronousCommands.CopyBuffer(
                slice,
                destination,
                destinationOffset,
                in ownedLease,
                owner);
        }
        return true;
    }

    private static EVulkanFrameDataLane ResolveFrameDataLane(
        Silk.NET.Vulkan.BufferUsageFlags usage)
    {
        if ((usage & Silk.NET.Vulkan.BufferUsageFlags.IndirectBufferBit) != 0)
            return EVulkanFrameDataLane.Indirect;
        if ((usage & Silk.NET.Vulkan.BufferUsageFlags.StorageBufferBit) != 0)
            return EVulkanFrameDataLane.Storage;
        if ((usage & Silk.NET.Vulkan.BufferUsageFlags.UniformBufferBit) != 0)
            return EVulkanFrameDataLane.Uniform;
        return EVulkanFrameDataLane.TransferUpload;
    }
}
