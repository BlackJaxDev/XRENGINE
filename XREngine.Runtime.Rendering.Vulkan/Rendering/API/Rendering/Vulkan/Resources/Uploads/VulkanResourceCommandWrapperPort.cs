using Silk.NET.Vulkan;
using System.Runtime.ExceptionServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow, generation-owned command entry point for backend wrappers. It owns no
/// renderer, output, or frame-planner reference; wrappers record directly through
/// its tracked encoder and wait before retiring transient staging resources.
/// </summary>
internal unsafe sealed class VulkanResourceCommandWrapperPort(
    VulkanBackendObjectContext context,
    VulkanCommandRuntime commandRuntime,
    VulkanResourceRuntime resourceRuntime,
    VulkanFrameTelemetry telemetry)
{
    private readonly VulkanBackendObjectContext _context = context;
    private readonly VulkanResourceRuntime _resourceRuntime = resourceRuntime;
    private readonly VulkanFrameTelemetry _telemetry = telemetry;

    internal VulkanCommandRuntime CommandRuntime { get; } = commandRuntime;

    internal VulkanSynchronousResourceCommandSession Begin(string owner)
        => new(_context, CommandRuntime, _resourceRuntime, _telemetry, owner);

    internal void CopyBuffer(
        Silk.NET.Vulkan.Buffer source,
        Silk.NET.Vulkan.Buffer destination,
        ulong size,
        ulong sourceOffset,
        ulong destinationOffset,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        BufferCopy copy = new()
        {
            SrcOffset = sourceOffset,
            DstOffset = destinationOffset,
            Size = size,
        };
        session.Encoder.CopyBuffer(session.CommandBuffer, source, destination, 1, &copy);
        session.CompleteAndWait();
    }

    internal void CopyBuffer(
        in VulkanFrameDataSlice sourceSlice,
        Silk.NET.Vulkan.Buffer destination,
        ulong destinationOffset,
        in VulkanSynchronousFrameDataArenaLease lease,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        BufferCopy copy = new()
        {
            SrcOffset = sourceSlice.Offset,
            DstOffset = destinationOffset,
            Size = sourceSlice.Length,
        };
        session.Encoder.CopyBuffer(
            session.CommandBuffer,
            sourceSlice.Buffer,
            destination,
            1,
            &copy);
        session.CompleteAndWait(lease.Arena, sourceSlice);
    }

    internal void CopyBufferToImage(
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout layout,
        ref BufferImageCopy region,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        fixed (BufferImageCopy* regionPtr = &region)
            session.Encoder.CopyBufferToImage(
                session.CommandBuffer,
                source,
                destination,
                layout,
                1,
                regionPtr);
        session.CompleteAndWait();
    }

    internal void CopyBufferToImage(
        in VulkanFrameDataSlice sourceSlice,
        Image destination,
        ImageLayout layout,
        ref BufferImageCopy region,
        in VulkanSynchronousFrameDataArenaLease lease,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        fixed (BufferImageCopy* regionPtr = &region)
            session.Encoder.CopyBufferToImage(
                session.CommandBuffer,
                sourceSlice.Buffer,
                destination,
                layout,
                1,
                regionPtr);
        session.CompleteAndWait(lease.Arena, sourceSlice);
    }

    internal void PipelineBarrier(
        PipelineStageFlags sourceStages,
        PipelineStageFlags destinationStages,
        uint imageBarrierCount,
        ImageMemoryBarrier* imageBarriers,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        session.Encoder.PipelineBarrier(
            session.CommandBuffer,
            sourceStages,
            destinationStages,
            0,
            0,
            null,
            0,
            null,
            imageBarrierCount,
            imageBarriers);
        session.CompleteAndWait();
    }

    internal void ClearColorImage(
        Image image,
        ImageLayout layout,
        ref ClearColorValue color,
        ref ImageSubresourceRange range,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        session.Encoder.ClearColorImage(session.CommandBuffer, image, layout, ref color, 1, ref range);
        session.CompleteAndWait();
    }

    internal void ClearDepthStencilImage(
        Image image,
        ImageLayout layout,
        ref ClearDepthStencilValue value,
        ref ImageSubresourceRange range,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        session.Encoder.Track(session.CommandBuffer, ObjectType.Image, image.Handle);
        _context.Api.CmdClearDepthStencilImage(session.CommandBuffer, image, layout, ref value, 1, ref range);
        session.CompleteAndWait();
    }

    internal void GenerateMipmaps(
        Image image,
        uint mipLevels,
        uint arrayLayers,
        ImageAspectFlags aspectMask,
        Extent3D extent,
        string owner)
    {
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = image,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspectMask,
                BaseArrayLayer = 0,
                LayerCount = arrayLayers,
                LevelCount = 1,
            },
        };
        int width = (int)extent.Width;
        int height = (int)extent.Height;
        for (uint level = 1; level < mipLevels; level++)
        {
            barrier.SubresourceRange.BaseMipLevel = level - 1;
            barrier.OldLayout = ImageLayout.TransferDstOptimal;
            barrier.NewLayout = ImageLayout.TransferSrcOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.TransferReadBit;
            session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);

            int destinationWidth = Math.Max(width / 2, 1);
            int destinationHeight = Math.Max(height / 2, 1);
            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers { AspectMask = aspectMask, MipLevel = level - 1, LayerCount = arrayLayers },
                DstSubresource = new ImageSubresourceLayers { AspectMask = aspectMask, MipLevel = level, LayerCount = arrayLayers },
            };
            blit.SrcOffsets.Element1 = new Offset3D(width, height, 1);
            blit.DstOffsets.Element1 = new Offset3D(destinationWidth, destinationHeight, 1);
            session.Encoder.BlitImage(session.CommandBuffer, image, ImageLayout.TransferSrcOptimal, image, ImageLayout.TransferDstOptimal, ref blit, Filter.Linear);

            barrier.OldLayout = ImageLayout.TransferSrcOptimal;
            barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferReadBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &barrier);
            width = destinationWidth;
            height = destinationHeight;
        }

        barrier.SubresourceRange.BaseMipLevel = mipLevels - 1;
        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags.ShaderReadBit;
        session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &barrier);
        session.CompleteAndWait();
    }

    internal bool TryDecompressBufferGDeflate(
        Silk.NET.Vulkan.Buffer source,
        ulong sourceOffset,
        ulong compressedSize,
        Silk.NET.Vulkan.Buffer destination,
        ulong destinationOffset,
        ulong decompressedSize,
        string owner)
    {
        if (!_context.Supports(EVulkanDeviceCapability.NvMemoryDecompression) ||
            !_context.Supports(EVulkanDeviceCapability.BufferDeviceAddress) ||
            compressedSize == 0 || decompressedSize == 0 ||
            _context.DeviceContext.ExtensionFunctions.NvMemoryDecompression is not { } decompression)
        {
            return false;
        }

        ulong methods = (ulong)_context.DeviceContext.MutableCapabilities._nvMemoryDecompressionMethods;
        if (methods == 0)
            return false;
        ulong sourceAddress = _context.Resources.Buffers.GetDeviceAddress(_context, source);
        ulong destinationAddress = _context.Resources.Buffers.GetDeviceAddress(_context, destination);
        if (sourceAddress == 0 || destinationAddress == 0)
            return false;

        DecompressMemoryRegionNV region = new()
        {
            SrcAddress = sourceAddress + sourceOffset,
            DstAddress = destinationAddress + destinationOffset,
            CompressedSize = compressedSize,
            DecompressedSize = decompressedSize,
            DecompressionMethod = (MemoryDecompressionMethodFlagsNV)(methods & (~methods + 1)),
        };
        try
        {
            using VulkanSynchronousResourceCommandSession session = Begin(owner);
            session.Encoder.Track(session.CommandBuffer, ObjectType.Buffer, source.Handle);
            session.Encoder.Track(session.CommandBuffer, ObjectType.Buffer, destination.Handle);
            decompression.CmdDecompressMemory(session.CommandBuffer, new ReadOnlySpan<DecompressMemoryRegionNV>(in region));
            session.CompleteAndWait();
            return true;
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_memory_decompression upload failed: {exception.Message}");
            return false;
        }
    }
}
