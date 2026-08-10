using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned Streamline UI image preparation.</summary>
internal sealed unsafe partial class VulkanCommandRuntime
{
    /// <summary>
    /// Clears and transitions a producer-frozen UI image for native DLSS-G recording.
    /// </summary>
    internal bool TryPrepareStreamlineUiImage(
        CommandBuffer commandBuffer,
        in VulkanStreamlineImage preparedImage,
        out VulkanStreamlineImage image)
    {
        image = preparedImage;
        if (preparedImage.Image.Handle == 0 || preparedImage.View.Handle == 0)
            return false;

        TransitionStreamlineUiImage(
            commandBuffer,
            preparedImage.Image,
            preparedImage.Layout,
            ImageLayout.TransferDstOptimal);

        ClearColorValue transparent = new(0f, 0f, 0f, 0f);
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        PrimaryCommandEncoder.ClearColorImage(
            commandBuffer,
            preparedImage.Image,
            ImageLayout.TransferDstOptimal,
            ref transparent,
            1,
            ref range);

        TransitionStreamlineUiImage(
            commandBuffer,
            preparedImage.Image,
            ImageLayout.TransferDstOptimal,
            ImageLayout.General);
        image = preparedImage with { Layout = ImageLayout.General };
        return true;
    }

    private void TransitionStreamlineUiImage(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        if (oldLayout == newLayout)
            return;

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = DlssFrameOp.ResolveAccessMask(oldLayout),
            DstAccessMask = DlssFrameOp.ResolveAccessMask(newLayout),
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            DlssFrameOp.ResolvePipelineStage(oldLayout),
            DlssFrameOp.ResolvePipelineStage(newLayout),
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }
}
