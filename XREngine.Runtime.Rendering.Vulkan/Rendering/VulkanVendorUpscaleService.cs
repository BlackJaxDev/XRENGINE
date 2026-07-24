using XREngine.Rendering.DLSS;
using XREngine.Rendering.XeSS;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanVendorUpscaleService : IRuntimeVendorUpscaleService
{
    public static VulkanVendorUpscaleService Instance { get; } = new();

    private VulkanVendorUpscaleService()
    {
    }

    public bool IsDlssSupported => NvidiaDlssManager.IsSupported;
    public string? DlssLastError => NvidiaDlssManager.LastError;
    public bool IsXessSupported => IntelXessManager.IsSupported;
    public string? XessLastError => IntelXessManager.LastError;
    public bool IsDlssFrameGenerationRequested => NvidiaDlssManager.IsFrameGenerationRequested;

    public bool IsDlssFrameGenerationAvailable(out string? failureReason)
        => NvidiaDlssManager.Native.IsFrameGenerationAvailable(out failureReason);

    public bool IsTerminalBridgeFailureMessage(string? failureReason)
        => NvidiaDlssManager.Native.IsTerminalBridgeFailureMessage(failureReason);

    public float GetDlssRecommendedRenderScale(object? settings = null)
        => NvidiaDlssManager.GetRecommendedRenderScale(settings);

    public float GetXessRecommendedRenderScale(object? settings = null)
        => IntelXessManager.GetRecommendedRenderScale(settings);

    public void ApplyXessToViewport(XRViewport viewport, object? settings = null)
        => IntelXessManager.ApplyToViewport(viewport, settings);

    public bool TryDispatchDlssUpscale(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRFrameBuffer? target,
        XRTexture? depth,
        XRTexture? motion,
        out int errorCode,
        out string? failureReason)
    {
        bool dispatched = NvidiaDlssManager.Native.TryDispatchUpscale(
            viewport,
            source,
            target,
            depth,
            motion,
            out errorCode);
        failureReason = dispatched ? null : NvidiaDlssManager.Native.LastError;
        return dispatched;
    }

    public bool TryDispatchXessUpscale(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRFrameBuffer? target,
        XRTexture? depth,
        XRTexture? motion,
        float sharpness,
        out int errorCode)
        => IntelXessManager.Native.TryDispatchUpscale(
            viewport,
            source,
            target,
            depth,
            motion,
            sharpness,
            out errorCode);

    public bool TryDispatchXessFrameGeneration(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRTexture? motion,
        out int errorCode,
        out string? failureReason)
        => IntelXessManager.Native.TryDispatchFrameGeneration(
            viewport,
            source,
            motion,
            out errorCode,
            out failureReason);

    public IVulkanUpscaleBridge CreateBridge(XRViewport viewport)
        => new VulkanUpscaleBridge(viewport);

    public VulkanUpscaleBridgeProbeSnapshot ProbeBridge(string? openGlVendor, string? openGlRenderer)
    {
        VulkanUpscaleBridgeProbeResult result = VulkanUpscaleBridgeProbe.Probe(
            openGlVendor,
            openGlRenderer);
        return new VulkanUpscaleBridgeProbeSnapshot(
            result.ProbeSucceeded,
            result.HasVulkanExternalMemoryImport,
            result.HasVulkanExternalSemaphoreImport,
            result.SelectedDeviceName,
            result.SelectedVendorId,
            result.SelectedDeviceId,
            result.SamePhysicalGpu,
            result.GpuIdentityReason,
            result.ProbeFailureReason);
    }
}
