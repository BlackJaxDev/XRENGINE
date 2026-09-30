namespace XREngine.Runtime.Bootstrap;

public class UnitTestingVrSettings
{
    public UnitTestingVrLaunchMode Mode { get; set; } = UnitTestingVrLaunchMode.Desktop;
    /// <summary>
    /// Requested VR eye rendering mode. OpenXR Vulkan SinglePassStereo strictly
    /// requires true layered multiview rendering; unavailable capabilities are
    /// logged and the XR output is not rendered. It never falls back to per-eye
    /// rendering. Logs and profile captures report the effective implementation.
    /// </summary>
    public EVrViewRenderMode ViewRenderMode { get; set; } = EVrViewRenderMode.SequentialViews;
    public bool PreviewStereoViews { get; set; } = false;
    public bool AllowDesktopEditing { get; set; } = true;
    public UnitTestingVrFoveationSettings Foveation { get; set; } = new();
    public UnitTestingOpenXrEyeResolutionSettings OpenXrEyeResolution { get; set; } = new();
    /// <summary>
    /// Optional process-scoped XR_RUNTIME_JSON manifest for OpenXR modes.
    /// Existing XR_RUNTIME_JSON environment values win. MonadoOpenXR auto-detects
    /// common Monado install/build locations when this is unset.
    /// </summary>
    public string? OpenXrRuntimeJson { get; set; }
}
