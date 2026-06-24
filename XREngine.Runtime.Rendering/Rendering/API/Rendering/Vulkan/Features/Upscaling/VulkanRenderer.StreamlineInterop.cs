using System;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanStreamlineImage(
        Image Image,
        DeviceMemory Memory,
        ImageView View,
        ImageLayout Layout,
        Format Format,
        ImageUsageFlags Usage,
        ImageAspectFlags Aspect,
        uint Width,
        uint Height,
        IVkFrameBufferAttachmentSource? LayoutTracker);

    internal bool TryResolveStreamlineImage(
        XRTexture texture,
        bool depthOnly,
        out VulkanStreamlineImage image,
        out string failureReason)
    {
        image = default;
        failureReason = string.Empty;

        if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
        {
            failureReason = $"Texture '{DescribeStreamlineTexture(texture)}' does not have a Vulkan image descriptor source.";
            return false;
        }

        Image vkImage = source.DescriptorImage;
        DeviceMemory memory = source.DescriptorMemory;
        ImageView view = depthOnly ? source.GetDepthOnlyDescriptorView() : source.DescriptorView;
        if (depthOnly && view.Handle == 0)
            view = source.DescriptorView;

        if (vkImage.Handle == 0)
        {
            failureReason = $"Texture '{DescribeStreamlineTexture(texture)}' resolved to a null VkImage.";
            return false;
        }

        if (memory.Handle == 0)
        {
            failureReason = $"Texture '{DescribeStreamlineTexture(texture)}' resolved to a null VkDeviceMemory.";
            return false;
        }

        if (view.Handle == 0)
        {
            failureReason = $"Texture '{DescribeStreamlineTexture(texture)}' resolved to a null VkImageView.";
            return false;
        }

        var size = texture.WidthHeightDepth;
        uint width = (uint)Math.Max(1, size.X);
        uint height = (uint)Math.Max(1, size.Y);
        ImageAspectFlags aspect = depthOnly
            ? source.DescriptorAspect & ImageAspectFlags.DepthBit
            : source.DescriptorAspect;
        if (aspect == ImageAspectFlags.None)
            aspect = source.DescriptorAspect;

        image = new VulkanStreamlineImage(
            vkImage,
            memory,
            view,
            source.TrackedImageLayout,
            source.DescriptorFormat,
            source.DescriptorUsage,
            aspect,
            width,
            height,
            source as IVkFrameBufferAttachmentSource);
        return true;
    }

    internal void EnqueueDlssUpscale(
        int passIndex,
        NvidiaDlssManager.Native.NativeVulkanSession session,
        in VulkanStreamlineImage sourceColor,
        in VulkanStreamlineImage depth,
        in VulkanStreamlineImage motion,
        in VulkanStreamlineImage outputColor,
        in VulkanStreamlineImage? exposure,
        in VulkanUpscaleBridgeDispatchParameters parameters)
    {
        FrameOpContext context = CaptureFrameOpContext();
        EnqueueFrameOp(new DlssUpscaleOp(
            passIndex,
            session,
            sourceColor,
            depth,
            motion,
            outputColor,
            exposure,
                parameters,
                context));
    }

    internal void EnqueueDlssFrameGeneration(
        int passIndex,
        NvidiaDlssManager.Native.NativeFrameGenerationSession session,
        in VulkanStreamlineImage depth,
        in VulkanStreamlineImage motion,
        in VulkanStreamlineImage hudlessColor,
        in VulkanUpscaleBridgeDispatchParameters parameters)
    {
        FrameOpContext context = CaptureFrameOpContext();
        EnqueueFrameOp(new DlssFrameGenerationOp(
            passIndex,
            session,
            depth,
            motion,
            hudlessColor,
            parameters,
            context));
    }

    private static string DescribeStreamlineTexture(XRTexture texture)
        => texture.Name ?? texture.SamplerName ?? texture.GetType().Name;
}
