using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>Vulkan vendor-upscale resource resolution and operation publication.</summary>
internal sealed unsafe partial class VulkanUpscaleBridgeSidecar
{
    internal static bool TryCreateDlssSession(
        in VulkanStreamlineDeviceBinding binding,
        uint viewportId,
        out IRuntimeVendorUpscaleSession? session,
        out string failureReason)
    {
        bool created = NvidiaDlssManager.Native.TryCreateNativeVulkanSession(
            binding,
            viewportId,
            out NvidiaDlssManager.Native.NativeVulkanSession? native,
            out failureReason);
        session = created && native is not null ? new VulkanDlssVendorUpscaleSession(native) : null;
        return session is not null;
    }

    internal static bool TryCreateFrameGenerationSession(
        in VulkanStreamlineDeviceBinding binding,
        in VulkanFrameGenerationOutputSnapshot output,
        uint viewportId,
        out IRuntimeVendorUpscaleSession? session,
        out string failureReason)
    {
        bool created = NvidiaDlssManager.Native.TryCreateNativeFrameGenerationSession(
            binding,
            output,
            viewportId,
            out NvidiaDlssManager.Native.NativeFrameGenerationSession? native,
            out failureReason);
        session = created && native is not null ? new VulkanFrameGenerationVendorUpscaleSession(native) : null;
        return session is not null;
    }

    internal static bool TryDispatchFrameGeneration(
        VulkanWrapperLookupPort wrapperLookup,
        XRViewport viewport,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        XRTexture depth,
        XRTexture motion,
        XRTexture hudlessColor,
        out int errorCode,
        out string? errorMessage)
    {
        if (!TryResolveStreamlineImage(wrapperLookup, depth, true, out VulkanStreamlineImage depthImage, out errorMessage)
            || !TryResolveStreamlineImage(wrapperLookup, motion, false, out VulkanStreamlineImage motionImage, out errorMessage)
            || !TryResolveStreamlineImage(wrapperLookup, hudlessColor, false, out VulkanStreamlineImage hudlessImage, out errorMessage))
        {
            errorCode = -1;
            return false;
        }

        return NvidiaDlssManager.Native.TryDispatchFrameGeneration(
            viewport,
            in parameters,
            in depthImage,
            in motionImage,
            in hudlessImage,
            NvidiaDlssManager.ResolveFrameGenerationMode(),
            out errorCode,
            out errorMessage);
    }

    internal static bool TryEnqueueDlssUpscale(
        VulkanWrapperLookupPort wrapperLookup,
        VulkanCommandRuntime commandRuntime,
        VulkanFrameOperationQueue operationQueue,
        int passIndex,
        IRuntimeVendorUpscaleSession session,
        XRTexture sourceColor,
        XRTexture depth,
        XRTexture motion,
        XRTexture outputColor,
        XRTexture? exposure,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        in FrameOpContext context,
        out string failureReason)
    {
        if (session is not VulkanDlssVendorUpscaleSession dlssSession)
        {
            failureReason = "The installed Vulkan backend does not own the supplied DLSS session.";
            return false;
        }
        if (!TryResolveStreamlineImage(wrapperLookup, sourceColor, false, out VulkanStreamlineImage sourceImage, out failureReason)
            || !TryResolveStreamlineImage(wrapperLookup, depth, true, out VulkanStreamlineImage depthImage, out failureReason)
            || !TryResolveStreamlineImage(wrapperLookup, motion, false, out VulkanStreamlineImage motionImage, out failureReason)
            || !TryResolveStreamlineImage(wrapperLookup, outputColor, false, out VulkanStreamlineImage outputImage, out failureReason))
            return false;

        VulkanStreamlineImage? exposureImage = null;
        if (exposure is not null)
        {
            if (!TryResolveStreamlineImage(wrapperLookup, exposure, false, out VulkanStreamlineImage resolvedExposure, out failureReason))
                return false;
            exposureImage = resolvedExposure;
        }

        commandRuntime.EnqueueFrameOperation(
            operationQueue,
            new DlssUpscaleOp(
                passIndex,
                dlssSession.Native,
                sourceImage,
                depthImage,
                motionImage,
                outputImage,
                exposureImage,
                parameters,
                context),
            passIndex);
        failureReason = string.Empty;
        return true;
    }

    internal static bool TryEnqueueFrameGeneration(
        VulkanWrapperLookupPort wrapperLookup,
        VulkanCommandRuntime commandRuntime,
        VulkanFrameOperationQueue operationQueue,
        int passIndex,
        IRuntimeVendorUpscaleSession session,
        XRTexture depth,
        XRTexture motion,
        XRTexture hudlessColor,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        in FrameOpContext context,
        out string failureReason)
    {
        if (session is not VulkanFrameGenerationVendorUpscaleSession frameGenerationSession)
        {
            failureReason = "The installed Vulkan backend does not own the supplied frame-generation session.";
            return false;
        }
        if (!TryResolveStreamlineImage(wrapperLookup, depth, true, out VulkanStreamlineImage depthImage, out failureReason)
            || !TryResolveStreamlineImage(wrapperLookup, motion, false, out VulkanStreamlineImage motionImage, out failureReason)
            || !TryResolveStreamlineImage(wrapperLookup, hudlessColor, false, out VulkanStreamlineImage hudlessImage, out failureReason))
            return false;

        commandRuntime.EnqueueFrameOperation(
            operationQueue,
            new DlssFrameGenerationOp(
                passIndex,
                frameGenerationSession.Native,
                depthImage,
                motionImage,
                hudlessImage,
                parameters,
                context),
            passIndex);
        failureReason = string.Empty;
        return true;
    }

    private static bool TryResolveStreamlineImage(
        VulkanWrapperLookupPort wrapperLookup,
        XRTexture texture,
        bool depthOnly,
        out VulkanStreamlineImage image,
        out string failureReason)
    {
        image = default;
        failureReason = string.Empty;
        if (wrapperLookup.GetOrCreate(texture, generateNow: true) is not IVkImageDescriptorSource source)
        {
            failureReason = $"Texture '{DescribeStreamlineTexture(texture)}' does not have a Vulkan image descriptor source.";
            return false;
        }

        Image vkImage = source.DescriptorImage;
        DeviceMemory memory = source.DescriptorMemory;
        ImageView view = depthOnly ? source.GetDepthOnlyDescriptorView() : source.DescriptorView;
        if (depthOnly && view.Handle == 0)
            view = source.DescriptorView;
        if (vkImage.Handle == 0 || memory.Handle == 0 || view.Handle == 0)
        {
            string missing = vkImage.Handle == 0
                ? "VkImage"
                : memory.Handle == 0
                    ? "VkDeviceMemory"
                    : "VkImageView";
            failureReason = $"Texture '{DescribeStreamlineTexture(texture)}' resolved to a null {missing}.";
            return false;
        }

        var size = texture.WidthHeightDepth;
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
            (uint)Math.Max(1, size.X),
            (uint)Math.Max(1, size.Y),
            source as IVkFrameBufferAttachmentSource);
        return true;
    }

    private static string DescribeStreamlineTexture(XRTexture texture)
        => texture.Name ?? texture.SamplerName ?? texture.GetType().Name;
}
