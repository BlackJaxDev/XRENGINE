using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe sealed partial class VulkanResourceCommandWrapperPort
{
    /// <summary>
    /// Copies immutable, completed GPU mip sources in one tracked graphics-queue
    /// transaction. The existing synchronous receipt retains all native resources
    /// through fence completion (including failed-wait retirement).
    /// </summary>
    internal void CopyTextureArrayLayers(
        Image destination,
        ulong destinationGeneration,
        ImageLayout destinationLayout,
        ImageLayout finalLayout,
        uint mipLevels,
        uint layers,
        ReadOnlySpan<VulkanTextureArrayCopyRegion> regions)
    {
        const string owner = "VkTexture2DArray.CopyGpuLayers";
        using VulkanSynchronousResourceCommandSession session = Begin(owner);
        // Validate exact generations before emitting any native command. A source
        // replaced after snapshot capture must reject rather than resolve a new image.
        CommandRuntime.TrackCommandBufferResource(session.CommandBuffer,
            new VulkanResourceLifetimeKey(ObjectType.Image, destination.Handle), owner, destinationGeneration);
        foreach (ref readonly VulkanTextureArrayCopyRegion region in regions)
            CommandRuntime.TrackCommandBufferResource(session.CommandBuffer,
                new VulkanResourceLifetimeKey(ObjectType.Image, region.Source.Handle), owner, region.SourceGeneration);

        ImageMemoryBarrier destinationBarrier = CreateArrayCopyBarrier(destination,
            destinationLayout, ImageLayout.TransferDstOptimal, 0, mipLevels, 0, layers,
            destinationLayout == ImageLayout.Undefined ? 0 : AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            AccessFlags.TransferWriteBit);
        session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &destinationBarrier);

        foreach (ref readonly VulkanTextureArrayCopyRegion region in regions)
        {
            ImageMemoryBarrier sourceBarrier = CreateArrayCopyBarrier(region.Source,
                region.SourceLayout, ImageLayout.TransferSrcOptimal, region.MipLevel, 1, 0, 1,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.TransferReadBit);
            session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &sourceBarrier);
            ImageCopy copy = new()
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, region.MipLevel, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, region.MipLevel, region.DestinationLayer, 1),
                Extent = region.Extent,
            };
            session.Encoder.CopyImage(session.CommandBuffer, region.Source, destination, ref copy);
            // Restore each source mip to its exact pre-copy layout. A prefilter's
            // mips can have different tracked layouts after convolution/readback.
            sourceBarrier.OldLayout = ImageLayout.TransferSrcOptimal;
            sourceBarrier.NewLayout = region.SourceLayout;
            sourceBarrier.SrcAccessMask = AccessFlags.TransferReadBit;
            sourceBarrier.DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
            session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.TransferBit,
                PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &sourceBarrier);
        }

        destinationBarrier.OldLayout = ImageLayout.TransferDstOptimal;
        destinationBarrier.NewLayout = finalLayout;
        destinationBarrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        destinationBarrier.DstAccessMask = AccessFlags.ShaderReadBit |
            (finalLayout == ImageLayout.General ? AccessFlags.ShaderWriteBit : 0);
        session.Encoder.PipelineBarrier(session.CommandBuffer, PipelineStageFlags.TransferBit,
            PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &destinationBarrier);
        session.CompleteAndWait();
    }

    private static ImageMemoryBarrier CreateArrayCopyBarrier(
        Image image, ImageLayout oldLayout, ImageLayout newLayout,
        uint baseMip, uint mipCount, uint baseLayer, uint layerCount,
        AccessFlags sourceAccess, AccessFlags destinationAccess)
        => new()
        {
            SType = StructureType.ImageMemoryBarrier,
            Image = image,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, baseMip, mipCount, baseLayer, layerCount),
        };
}
