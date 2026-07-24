namespace XREngine.Rendering;

/// <summary>
/// Null-safe kernel façade over the optional leaf-owned vendor-upscale runtime.
/// </summary>
internal static class VendorUpscaleRuntime
{
    public static bool IsDlssSupported => RuntimeVendorUpscaleService.Current?.IsDlssSupported == true;
    public static string? DlssLastError => RuntimeVendorUpscaleService.Current?.DlssLastError;
    public static bool IsXessSupported => RuntimeVendorUpscaleService.Current?.IsXessSupported == true;
    public static string? XessLastError => RuntimeVendorUpscaleService.Current?.XessLastError;
    public static bool IsDlssFrameGenerationRequested =>
        RuntimeVendorUpscaleService.Current?.IsDlssFrameGenerationRequested == true;

    public static float GetDlssRecommendedRenderScale(object? settings = null)
        => RuntimeVendorUpscaleService.Current?.GetDlssRecommendedRenderScale(settings) ?? 1.0f;

    public static float GetXessRecommendedRenderScale(object? settings = null)
        => RuntimeVendorUpscaleService.Current?.GetXessRecommendedRenderScale(settings) ?? 1.0f;

    public static void ApplyXessToViewport(XRViewport viewport, object? settings = null)
        => RuntimeVendorUpscaleService.Current?.ApplyXessToViewport(viewport, settings);

    public static bool IsDlssFrameGenerationAvailable(out string? failureReason)
    {
        IRuntimeVendorUpscaleService? service = RuntimeVendorUpscaleService.Current;
        if (service is not null)
            return service.IsDlssFrameGenerationAvailable(out failureReason);

        failureReason = "The Vulkan vendor-upscale module is not registered.";
        return false;
    }

    public static bool IsTerminalBridgeFailureMessage(string? failureReason)
        => RuntimeVendorUpscaleService.Current?.IsTerminalBridgeFailureMessage(failureReason) == true;

    public static bool TryDispatchDlssUpscale(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRFrameBuffer? target,
        XRTexture? depth,
        XRTexture? motion,
        out int errorCode,
        out string? failureReason)
    {
        IRuntimeVendorUpscaleService? service = RuntimeVendorUpscaleService.Current;
        if (service is not null)
            return service.TryDispatchDlssUpscale(
                viewport,
                source,
                target,
                depth,
                motion,
                out errorCode,
                out failureReason);

        errorCode = -1;
        failureReason = "The Vulkan vendor-upscale module is not registered.";
        return false;
    }

    public static bool TryDispatchXessUpscale(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRFrameBuffer? target,
        XRTexture? depth,
        XRTexture? motion,
        float sharpness,
        out int errorCode)
    {
        IRuntimeVendorUpscaleService? service = RuntimeVendorUpscaleService.Current;
        if (service is not null)
            return service.TryDispatchXessUpscale(
                viewport,
                source,
                target,
                depth,
                motion,
                sharpness,
                out errorCode);

        errorCode = -1;
        return false;
    }

    public static bool TryDispatchXessFrameGeneration(
        XRViewport viewport,
        XRQuadFrameBuffer source,
        XRTexture? motion,
        out int errorCode,
        out string? failureReason)
    {
        IRuntimeVendorUpscaleService? service = RuntimeVendorUpscaleService.Current;
        if (service is not null)
            return service.TryDispatchXessFrameGeneration(
                viewport,
                source,
                motion,
                out errorCode,
                out failureReason);

        errorCode = -1;
        failureReason = "The Vulkan vendor-upscale module is not registered.";
        return false;
    }
}
