using System;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Tries to resolve a Vulkan streamline image from the given XRTexture.
    /// </summary>
    /// <param name="texture">The XRTexture to resolve.</param>
    /// <param name="depthOnly">Indicates whether to resolve a depth-only view of the image.</param>
    /// <param name="image">The resolved Vulkan streamline image if successful.</param>
    /// <param name="failureReason">The reason for failure if the resolution was unsuccessful.</param>
    /// <returns>True if the Vulkan streamline image was successfully resolved; otherwise, false.</returns>
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

    /// <summary>
    /// Enqueues a DLSS upscale operation with the specified parameters.
    /// </summary>
    /// <param name="passIndex">The index of the rendering pass.</param>
    /// <param name="session">The native Vulkan DLSS session.</param>
    /// <param name="sourceColor">The source color image.</param>
    /// <param name="depth">The depth image.</param>
    /// <param name="motion">The motion vector image.</param>
    /// <param name="outputColor">The output color image.</param>
    /// <param name="exposure">The optional exposure image.</param>
    /// <param name="parameters">The dispatch parameters for the upscale operation.</param>
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

    /// <summary>
    /// Enqueues a DLSS frame generation operation with the specified parameters.
    /// </summary>
    /// <param name="passIndex">The index of the rendering pass.</param>
    /// <param name="session">The native Vulkan DLSS frame generation session.</param>
    /// <param name="depth">The depth image.</param>
    /// <param name="motion">The motion vector image.</param>
    /// <param name="hudlessColor">The HUD-less color image.</param>
    /// <param name="parameters">The dispatch parameters for the frame generation operation.</param>
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

    /// <summary>
    /// Describes the specified streamline texture by returning its name, sampler name, or type name.
    /// </summary>
    /// <param name="texture">The streamline texture to describe.</param>
    /// <returns>A string describing the texture, which is either its name, sampler name, or type name.</returns>
    private static string DescribeStreamlineTexture(XRTexture texture)
        => texture.Name ?? texture.SamplerName ?? texture.GetType().Name;
}
