using System;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Base operation for Streamline-backed DLSS command recording.
/// </summary>
internal abstract unsafe record DlssFrameOp(
    int PassIndex,
    FrameOpContext Context)
    : FrameOp(PassIndex, null, Context)
{
    protected abstract string CommandLabel { get; }

    internal sealed override int RecordPrimary(
        VulkanRenderer renderer,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        renderer.CmdBeginLabel(recordingState.CommandBuffer, CommandLabel);
        RecordStreamlineCommand(
            renderer,
            recordingState.CommandBuffer,
            recordingState.ImageIndex);
        renderer.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }

    protected abstract void RecordStreamlineCommand(
        VulkanRenderer renderer,
        CommandBuffer commandBuffer,
        uint imageIndex);

    /// <summary>
    /// Transitions a Streamline input or output image to the general layout
    /// required by the native DLSS Vulkan bridge.
    /// </summary>
    protected static VulkanStreamlineImage TransitionImageToGeneral(
        VulkanRenderer renderer,
        CommandBuffer commandBuffer,
        in VulkanStreamlineImage image)
    {
        if (image.Image.Handle == 0)
            return image;

        // The Vulkan bridge requires that all Streamline images be in the general layout
        // when passed to the native DLSS Vulkan bridge. The Vulkan bridge will internally
        // transition the images to the appropriate layouts for the DLSS operations, but
        // we must ensure they are in the general layout before invoking the bridge.
        ImageLayout oldLayout = image.Layout;
        if (oldLayout != ImageLayout.General)
        {
            // Create an image memory barrier to use to transition the image to the general layout.
            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = ResolveAccessMask(oldLayout),
                DstAccessMask =
                    AccessFlags.MemoryReadBit |
                    AccessFlags.MemoryWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = image.Aspect == ImageAspectFlags.None
                        ? ImageAspectFlags.ColorBit
                        : image.Aspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };

            // Issue a pipeline barrier to transition the image to the general layout.
            renderer.CmdPipelineBarrierTracked(
                commandBuffer,
                ResolvePipelineStage(oldLayout),
                PipelineStageFlags.AllCommandsBit,
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                1,
                &barrier);
        }

        // Update the tracked layout of the image to general, so that subsequent operations know its current layout.
        image.LayoutTracker?.UpdateTrackedLayout(ImageLayout.General);

        // Return a new VulkanStreamlineImage with the updated layout.
        return image with { Layout = ImageLayout.General };
    }

    /// <summary>
    /// Makes a Streamline output visible to subsequent shader sampling.
    /// </summary>
    protected static void MakeOutputVisibleForSampling(
        VulkanRenderer renderer,
        CommandBuffer commandBuffer,
        in VulkanStreamlineImage image)
    {
        if (image.Image.Handle == 0)
            return;

        // The Vulkan bridge requires that all Streamline output images be transitioned 
        // to a layout that allows shader sampling after the DLSS operation. 
        // We will transition the image to the general layout, 
        // which is suitable for shader sampling.
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask =
                AccessFlags.MemoryWriteBit |
                AccessFlags.ShaderWriteBit,
            DstAccessMask =
                AccessFlags.MemoryReadBit |
                AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = image.Aspect == ImageAspectFlags.None
                    ? ImageAspectFlags.ColorBit
                    : image.Aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        // Issue a pipeline barrier to make the output image visible for shader sampling.
        renderer.CmdPipelineBarrierTracked(
            commandBuffer,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.FragmentShaderBit |
            PipelineStageFlags.ComputeShaderBit,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);

        // Update the tracked layout of the image to general, so that subsequent operations know its current layout.
        image.LayoutTracker?.UpdateTrackedLayout(ImageLayout.General);
    }

    /// <summary>
    /// Throws an InvalidOperationException with a detailed message if a DLSS operation fails during Vulkan command recording.
    /// </summary>
    /// <param name="operation">The DLSS operation that failed.</param>
    /// <param name="failureReason">The reason for the failure, if available.</param>
    /// <exception cref="InvalidOperationException">Thrown if the DLSS operation fails during Vulkan command recording.</exception>
    protected static void ThrowRecordingFailure(
        string operation,
        string failureReason)
    {
        // The failure reason may be empty or null, so we provide a default message if it is.
        string reason = string.IsNullOrWhiteSpace(failureReason)
            ? "Streamline returned an unspecified failure."
            : failureReason;

        // Log the error and throw an exception with a detailed message.
        string message = $"Requested NVIDIA DLSS {operation} failed during Vulkan command recording: {reason}";
        Debug.RenderingError(message);
        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Resolves the appropriate access mask for a given image layout, which is used in Vulkan image memory barriers.
    /// </summary>
    /// <param name="layout">The image layout for which to resolve the access mask.</param>
    /// <returns>The appropriate access mask for the given image layout.</returns>
    internal static AccessFlags ResolveAccessMask(ImageLayout layout)
        => layout switch
        {
            ImageLayout.Undefined => 0,
            ImageLayout.ColorAttachmentOptimal =>
                AccessFlags.ColorAttachmentReadBit |
                AccessFlags.ColorAttachmentWriteBit,
            ImageLayout.DepthStencilAttachmentOptimal =>
                AccessFlags.DepthStencilAttachmentReadBit |
                AccessFlags.DepthStencilAttachmentWriteBit,
            ImageLayout.DepthStencilReadOnlyOptimal =>
                AccessFlags.DepthStencilAttachmentReadBit |
                AccessFlags.ShaderReadBit,
            ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
            ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
            ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
            ImageLayout.General =>
                AccessFlags.MemoryReadBit |
                AccessFlags.MemoryWriteBit,
            _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };

    /// <summary>
    /// Resolves the appropriate pipeline stage flags for a given image layout, which is used in Vulkan pipeline barriers.
    /// </summary>
    /// <param name="layout">The image layout for which to resolve the pipeline stage flags.</param>
    /// <returns>The appropriate pipeline stage flags for the given image layout.</returns>
    internal static PipelineStageFlags ResolvePipelineStage(ImageLayout layout)
        => layout switch
        {
            ImageLayout.Undefined => 
                PipelineStageFlags.TopOfPipeBit,

            ImageLayout.ColorAttachmentOptimal => 
                PipelineStageFlags.ColorAttachmentOutputBit,

            ImageLayout.DepthStencilAttachmentOptimal =>
                PipelineStageFlags.EarlyFragmentTestsBit |
                PipelineStageFlags.LateFragmentTestsBit,
            
            ImageLayout.DepthStencilReadOnlyOptimal =>
                PipelineStageFlags.EarlyFragmentTestsBit |
                PipelineStageFlags.LateFragmentTestsBit |
                PipelineStageFlags.FragmentShaderBit,
            
            ImageLayout.ShaderReadOnlyOptimal =>
                PipelineStageFlags.FragmentShaderBit |
                PipelineStageFlags.ComputeShaderBit,
            
            ImageLayout.TransferSrcOptimal or 
            ImageLayout.TransferDstOptimal => 
                PipelineStageFlags.TransferBit,

            ImageLayout.General => 
                PipelineStageFlags.AllCommandsBit,

            _ => PipelineStageFlags.AllCommandsBit,
        };
}
