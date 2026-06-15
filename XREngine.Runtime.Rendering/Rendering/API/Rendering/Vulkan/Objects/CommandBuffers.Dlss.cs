using System;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
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
    }

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
