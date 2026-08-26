using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal unsafe abstract partial class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
{
    #region Staging Buffers

    /// <summary>
    /// Copies ordinary CPU texture data into submit-and-wait scratch storage.
    /// </summary>
    protected bool UploadStagingDataToImage(
        DataSource? data,
        uint mipLevel,
        uint baseArrayLayer,
        uint layerCount,
        Extent3D extent)
    {
        if (data is null || data.Length == 0)
            return false;
        if ((ulong)data.Length > uint.MaxValue)
            throw new InvalidOperationException($"Texture staging upload is too large for the frame-data arena: {data.Length:N0} bytes.");

        if (!BackendContext.Resources.TryAcquireSynchronousFrameDataArenaLease(out VulkanSynchronousFrameDataArenaLease lease))
            throw new InvalidOperationException("The Vulkan synchronous frame-data arena is unavailable for a texture upload.");
        using (lease)
        {
            if (!lease.Arena.TryAllocateWrite(0, EVulkanFrameDataLane.TransferStaging, data.Address.Pointer, 0, (uint)data.Length, 16, out VulkanFrameDataSlice slice))
                throw new InvalidOperationException($"Vulkan frame-data arena rejected {data.Length:N0} texture staging bytes for slot 0.");

            CopyBufferToImage(slice, lease, mipLevel, baseArrayLayer, layerCount, extent);
            if (!lease.TryComplete(slice))
                throw new InvalidOperationException("Vulkan synchronous texture upload did not complete its frame-data staging slot.");
            return true;
        }
    }

    protected bool UploadStagingBytesToImage(
        ReadOnlySpan<byte> data,
        uint mipLevel,
        uint baseArrayLayer,
        uint layerCount,
        Extent3D extent)
    {
        if (data.IsEmpty)
            return false;
        if (!BackendContext.Resources.TryAcquireSynchronousFrameDataArenaLease(out VulkanSynchronousFrameDataArenaLease lease))
            throw new InvalidOperationException("The Vulkan synchronous frame-data arena is unavailable for a texture upload.");
        using (lease)
        {
            if (!lease.Arena.TryAllocateWrite(0, EVulkanFrameDataLane.TransferUpload, data, 16, out VulkanFrameDataSlice slice))
                throw new InvalidOperationException($"Vulkan frame-data arena rejected {data.Length:N0} texture staging bytes for slot 0.");

            CopyBufferToImage(slice, lease, mipLevel, baseArrayLayer, layerCount, extent);
            if (!lease.TryComplete(slice))
                throw new InvalidOperationException("Vulkan synchronous texture upload did not complete its frame-data staging slot.");
            return true;
        }
    }

    /// <summary>
    /// Acquires a pooled whole-buffer staging resource for an imported upload
    /// whose lifetime spans an independently fenced transfer submission.
    /// </summary>
    protected bool TryAllocateImportedStagingBuffer(
        DataSource? data,
        out Buffer buffer,
        out DeviceMemory memory,
        bool foregroundRequired = false)
    {
        if (data is null || data.Length == 0)
        {
            buffer = default;
            memory = default;
            return false;
        }

        (buffer, memory) = BackendContext.Resources.Allocations.Staging.Acquire(
            BackendContext,
            (ulong)data.Length,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            data.Address,
            foregroundRequired);
        return buffer.Handle != 0;
    }

    #endregion
}
