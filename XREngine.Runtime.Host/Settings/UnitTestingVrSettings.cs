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
    /// <summary>
    /// Requested VR eye pipeline family. Only this family renders the eyes. An
    /// unsupported pair with <see cref="ViewRenderMode"/> renders no XR output
    /// and logs a diagnostic; no other pipeline is used.
    /// </summary>
    public EVrRenderPipeline RenderPipeline { get; set; } = EVrRenderPipeline.Default;
    /// <summary>
    /// Requested RVC mode for <see cref="EVrRenderPipeline.Rvc"/>. Off, or a
    /// mode that the RVC resolver cannot run, renders no XR output.
    /// </summary>
    public ERvcPipelineMode RvcPipelineMode { get; set; } = ERvcPipelineMode.Off;
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
