using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral access to optional vendor-upscale runtimes owned by a rendering leaf.
/// </summary>
internal interface IRuntimeVendorUpscaleService
{
    bool IsDlssSupported { get; }
    string? DlssLastError { get; }
    bool AreDlssRuntimeLibrariesAvailable { get; }
    bool IsXessSupported { get; }
    string? XessLastError { get; }
    bool IsDlssFrameGenerationRequested { get; }
    bool IsDlssFrameGenerationSupported { get; }
    string? DlssFrameGenerationUnavailableReason { get; }
    bool IsDlssFrameGenerationAvailable(out string? failureReason);
    bool IsTerminalBridgeFailureMessage(string? failureReason);

    float GetDlssRecommendedRenderScale(object? settings = null);
    float GetXessRecommendedRenderScale(object? settings = null);
    void ApplyDlssToViewport(XRViewport viewport, object? settings = null);
    void ResetDlssViewport(XRViewport viewport);
    void ApplyXessToViewport(XRViewport viewport, object? settings = null);
    void ResetXessViewport(XRViewport viewport);

    bool TryDispatchDlssUpscale(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRFrameBuffer? target,
        XRTexture? depth,
        XRTexture? motion,
        out int errorCode,
        out string? failureReason);

    bool TryDispatchXessUpscale(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRFrameBuffer? target,
        XRTexture? depth,
        XRTexture? motion,
        float sharpness,
        out int errorCode);

    bool TryDispatchXessFrameGeneration(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRTexture? motion,
        out int errorCode,
        out string? failureReason);

    IVulkanUpscaleBridge CreateBridge(XRViewport viewport);
    VulkanUpscaleBridgeProbeSnapshot ProbeBridge(string? openGlVendor, string? openGlRenderer);
}
