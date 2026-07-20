using System;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Records a DLSS upscale operation into the specified Vulkan command buffer.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer to record the operation into.</param>
    /// <param name="op">The DLSS upscale operation to record.</param>
    /// <exception cref="InvalidOperationException">Thrown if the DLSS upscale operation fails during Vulkan command recording.</exception>
    private void RecordDlssUpscaleOp(CommandBuffer commandBuffer, DlssUpscaleOp op)
    {
        VulkanStreamlineImage sourceColor = TransitionStreamlineImageToGeneral(commandBuffer, op.SourceColor);
        VulkanStreamlineImage depth = TransitionStreamlineImageToGeneral(commandBuffer, op.Depth);
        VulkanStreamlineImage motion = TransitionStreamlineImageToGeneral(commandBuffer, op.Motion);
        VulkanStreamlineImage outputColor = TransitionStreamlineImageToGeneral(commandBuffer, op.OutputColor);
        VulkanStreamlineImage? exposure = op.Exposure.HasValue
            ? TransitionStreamlineImageToGeneral(commandBuffer, op.Exposure.Value)
            : null;
        VulkanUpscaleBridgeDispatchParameters parameters = op.Parameters;

        if (!NvidiaDlssManager.Native.TryRecordNativeVulkanUpscale(
                op.Session,
                commandBuffer,
                sourceColor,
                depth,
                motion,
                outputColor,
                exposure,
                in parameters,
                out string failureReason))
        {
            string message = string.IsNullOrWhiteSpace(failureReason)
                ? "Streamline returned an unspecified failure."
                : failureReason;
            Debug.RenderingError($"Requested NVIDIA DLSS upscale failed during Vulkan command recording: {message}");
            throw new InvalidOperationException($"Requested NVIDIA DLSS upscale failed during Vulkan command recording: {message}");
        }

        MakeStreamlineOutputVisibleForSampling(commandBuffer, outputColor);
    }

    /// <summary>
    /// Records a DLSS frame generation operation into the specified Vulkan command buffer.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer to record the operation into.</param>
    /// <param name="op">The DLSS frame generation operation to record.</param>
    /// <exception cref="InvalidOperationException">Thrown if the DLSS frame generation operation fails during Vulkan command recording.</exception>
    private void RecordDlssFrameGenerationOp(CommandBuffer commandBuffer, uint imageIndex, DlssFrameGenerationOp op)
    {
        // A render-resource generation can still contain the last queued DLSS-G op while a runtime
        // preference change is committing the non-DLSS-G generation. Do not feed Streamline an Off
        // mode (zero generated frames); the explicit preference-change path disables the feature.
        if (!NvidiaDlssManager.IsFrameGenerationRequested)
            return;

        VulkanStreamlineImage depth = TransitionStreamlineImageToGeneral(commandBuffer, op.Depth);
        VulkanStreamlineImage motion = TransitionStreamlineImageToGeneral(commandBuffer, op.Motion);
        VulkanStreamlineImage hudlessColor = TransitionStreamlineImageToGeneral(commandBuffer, op.HudlessColor);
        if (!TryPrepareStreamlineUiImage(commandBuffer, imageIndex, out VulkanStreamlineImage uiColorAndAlpha))
            throw new InvalidOperationException($"DLSS frame generation requires a UI color/alpha image for swapchain image {imageIndex}.");

        VulkanUpscaleBridgeDispatchParameters parameters = op.Parameters;

        if (!NvidiaDlssManager.Native.TryRecordNativeVulkanFrameGeneration(
                op.Session,
                commandBuffer,
                depth,
                motion,
                hudlessColor,
                uiColorAndAlpha,
                in parameters,
                out string failureReason))
        {
            string message = string.IsNullOrWhiteSpace(failureReason)
                ? "Streamline returned an unspecified failure."
                : failureReason;
            Debug.RenderingError($"Requested NVIDIA DLSS frame generation failed during Vulkan command recording: {message}");
            throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation failed during Vulkan command recording: {message}");
        }
    }

    /// <summary>
    /// Transitions the specified Vulkan streamline image to the general layout.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer used for the transition.</param>
    /// <param name="image">The Vulkan streamline image to transition.</param>
    /// <returns>The Vulkan streamline image with its layout transitioned to general.</returns>
    private VulkanStreamlineImage TransitionStreamlineImageToGeneral(CommandBuffer commandBuffer, in VulkanStreamlineImage image)
    {
        if (image.Image.Handle == 0)
            return image;

        ImageLayout oldLayout = image.Layout == ImageLayout.Undefined
            ? ImageLayout.Undefined
            : image.Layout;

        if (oldLayout != ImageLayout.General)
        {
            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = ResolveStreamlineAccessMask(oldLayout),
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = image.Aspect == ImageAspectFlags.None ? ImageAspectFlags.ColorBit : image.Aspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                ResolveStreamlinePipelineStage(oldLayout),
                PipelineStageFlags.AllCommandsBit,
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                1,
                &barrier);
        }

        image.LayoutTracker?.UpdateTrackedLayout(ImageLayout.General);
        return image with { Layout = ImageLayout.General };
    }

    /// <summary>
    /// Makes the specified Vulkan streamline image visible for sampling by transitioning its layout appropriately.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer used for the transition.</param>
    /// <param name="image">The Vulkan streamline image to make visible for sampling.</param>
    private void MakeStreamlineOutputVisibleForSampling(CommandBuffer commandBuffer, in VulkanStreamlineImage image)
    {
        if (image.Image.Handle == 0)
            return;

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = image.Aspect == ImageAspectFlags.None ? ImageAspectFlags.ColorBit : image.Aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);

        image.LayoutTracker?.UpdateTrackedLayout(ImageLayout.General);
    }

    /// <summary>
    /// Resolves the appropriate Vulkan access flags for the given image layout.
    /// </summary>
    /// <param name="layout">The Vulkan image layout for which to resolve access flags.</param>
    /// <returns>The Vulkan access flags corresponding to the specified image layout.</returns>
    private static AccessFlags ResolveStreamlineAccessMask(ImageLayout layout)
        => layout switch
        {
            ImageLayout.Undefined => 0,
            ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
            ImageLayout.DepthStencilAttachmentOptimal => AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
            ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.ShaderReadBit,
            ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
            ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
            ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
            ImageLayout.General => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };

    /// <summary>
    /// Resolves the appropriate Vulkan pipeline stage flags for the given image layout.
    /// </summary>
    /// <param name="layout">The Vulkan image layout for which to resolve pipeline stage flags.</param>
    /// <returns>The Vulkan pipeline stage flags corresponding to the specified image layout.</returns>
    private static PipelineStageFlags ResolveStreamlinePipelineStage(ImageLayout layout)
        => layout switch
        {
            ImageLayout.Undefined => PipelineStageFlags.TopOfPipeBit,
            ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
            ImageLayout.DepthStencilAttachmentOptimal => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            ImageLayout.DepthStencilReadOnlyOptimal => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit | PipelineStageFlags.FragmentShaderBit,
            ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
            ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
            ImageLayout.General => PipelineStageFlags.AllCommandsBit,
            _ => PipelineStageFlags.AllCommandsBit,
        };
}
