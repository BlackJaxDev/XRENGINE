using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer : IVulkanVendorUpscaleBackendCapability
{
    ulong IVulkanVendorUpscaleBackendCapability.FrameIndex
        => VulkanFrameCounter;

    bool IVulkanVendorUpscaleBackendCapability.TryCreateDlssSession(
        uint viewportId,
        out IRuntimeVendorUpscaleSession? session,
        out string failureReason)
    {
        bool created = NvidiaDlssManager.Native.TryCreateNativeVulkanSession(
            this,
            viewportId,
            out NvidiaDlssManager.Native.NativeVulkanSession? nativeSession,
            out failureReason);
        session = created && nativeSession is not null
            ? new VulkanDlssVendorUpscaleSession(nativeSession)
            : null;
        return session is not null;
    }

    bool IVulkanVendorUpscaleBackendCapability.TryCreateFrameGenerationSession(
        uint viewportId,
        out IRuntimeVendorUpscaleSession? session,
        out string failureReason)
    {
        bool created = NvidiaDlssManager.Native.TryCreateNativeFrameGenerationSession(
            this,
            viewportId,
            out NvidiaDlssManager.Native.NativeFrameGenerationSession? nativeSession,
            out failureReason);
        session = created && nativeSession is not null
            ? new VulkanFrameGenerationVendorUpscaleSession(nativeSession)
            : null;
        return session is not null;
    }

    bool IVulkanVendorUpscaleBackendCapability.TryDispatchFrameGeneration(
        XRViewport viewport,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        XRTexture depth,
        XRTexture motion,
        XRTexture hudlessColor,
        out int errorCode,
        out string? errorMessage)
    {
        if (!TryResolveStreamlineImage(depth, depthOnly: true, out VulkanStreamlineImage depthImage, out errorMessage) ||
            !TryResolveStreamlineImage(motion, depthOnly: false, out VulkanStreamlineImage motionImage, out errorMessage) ||
            !TryResolveStreamlineImage(hudlessColor, depthOnly: false, out VulkanStreamlineImage hudlessImage, out errorMessage))
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

    bool IVulkanVendorUpscaleBackendCapability.TryEnqueueDlssUpscale(
        int passIndex,
        IRuntimeVendorUpscaleSession session,
        XRTexture sourceColor,
        XRTexture depth,
        XRTexture motion,
        XRTexture outputColor,
        XRTexture? exposure,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out string failureReason)
    {
        if (session is not VulkanDlssVendorUpscaleSession dlssSession)
        {
            failureReason = "The installed Vulkan backend does not own the supplied DLSS session.";
            return false;
        }

        if (!TryResolveStreamlineImage(sourceColor, false, out VulkanStreamlineImage sourceImage, out failureReason) ||
            !TryResolveStreamlineImage(depth, true, out VulkanStreamlineImage depthImage, out failureReason) ||
            !TryResolveStreamlineImage(motion, false, out VulkanStreamlineImage motionImage, out failureReason) ||
            !TryResolveStreamlineImage(outputColor, false, out VulkanStreamlineImage outputImage, out failureReason))
            return false;

        VulkanStreamlineImage? exposureImage = null;
        if (exposure is not null)
        {
            if (!TryResolveStreamlineImage(exposure, false, out VulkanStreamlineImage resolvedExposure, out failureReason))
                return false;
            exposureImage = resolvedExposure;
        }

        EnqueueDlssUpscale(
            passIndex,
            dlssSession.Native,
            sourceImage,
            depthImage,
            motionImage,
            outputImage,
            exposureImage,
            parameters);
        failureReason = string.Empty;
        return true;
    }

    bool IVulkanVendorUpscaleBackendCapability.TryEnqueueFrameGeneration(
        int passIndex,
        IRuntimeVendorUpscaleSession session,
        XRTexture depth,
        XRTexture motion,
        XRTexture hudlessColor,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out string failureReason)
    {
        if (session is not VulkanFrameGenerationVendorUpscaleSession frameGenerationSession)
        {
            failureReason = "The installed Vulkan backend does not own the supplied frame-generation session.";
            return false;
        }

        if (!TryResolveStreamlineImage(depth, true, out VulkanStreamlineImage depthImage, out failureReason) ||
            !TryResolveStreamlineImage(motion, false, out VulkanStreamlineImage motionImage, out failureReason) ||
            !TryResolveStreamlineImage(hudlessColor, false, out VulkanStreamlineImage hudlessImage, out failureReason))
            return false;

        EnqueueDlssFrameGeneration(
            passIndex,
            frameGenerationSession.Native,
            depthImage,
            motionImage,
            hudlessImage,
            parameters);
        failureReason = string.Empty;
        return true;
    }

}
