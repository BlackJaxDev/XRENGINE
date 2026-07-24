using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

/// <summary>
/// Keeps Vulkan Streamline resources, native sessions, and image wrappers behind the backend boundary.
/// </summary>
internal interface IVulkanVendorUpscaleBackendCapability
{
    ulong FrameIndex { get; }

    bool TryCreateDlssSession(
        uint viewportId,
        out IRuntimeVendorUpscaleSession? session,
        out string failureReason);

    bool TryCreateFrameGenerationSession(
        uint viewportId,
        out IRuntimeVendorUpscaleSession? session,
        out string failureReason);

    bool TryDispatchFrameGeneration(
        XRViewport viewport,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        XRTexture depth,
        XRTexture motion,
        XRTexture hudlessColor,
        out int errorCode,
        out string? errorMessage);

    bool TryEnqueueDlssUpscale(
        int passIndex,
        IRuntimeVendorUpscaleSession session,
        XRTexture sourceColor,
        XRTexture depth,
        XRTexture motion,
        XRTexture outputColor,
        XRTexture? exposure,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out string failureReason);

    bool TryEnqueueFrameGeneration(
        int passIndex,
        IRuntimeVendorUpscaleSession session,
        XRTexture depth,
        XRTexture motion,
        XRTexture hudlessColor,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out string failureReason);
}
