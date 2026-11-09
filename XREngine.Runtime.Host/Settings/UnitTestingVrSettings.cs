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
    public EVrViewRenderMode ViewRenderMode { get; set; } = EVrViewRenderMode.ParallelCommandBufferRecording;
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
    /// <summary>
    /// Requests the OpenXR quad-view configuration (wide and inset view per eye) for
    /// <see cref="EVrRenderPipeline.Rvc"/>. The runtime must expose XR_VARJO_quad_views.
    /// </summary>
    public bool RvcQuadView { get; set; } = false;
    public bool PreviewStereoViews { get; set; } = false;
    public bool AllowDesktopEditing { get; set; } = true;
    /// <summary>
    /// Desktop view that the editor shows when an OpenXR session starts with
    /// <see cref="AllowDesktopEditing"/> on. With
    /// <see cref="XREngine.Components.VR.EVrDesktopView.EditorCamera"/>, the
    /// desktop player controls the editor camera, and VR action input such as
    /// thumbstick locomotion does not reach the VR pawn. Headset and controller
    /// tracking continue. The other views keep input on the VR pawn.
    /// </summary>
    public XREngine.Components.VR.EVrDesktopView DesktopView { get; set; } = XREngine.Components.VR.EVrDesktopView.EditorCamera;
    public UnitTestingVrFoveationSettings Foveation { get; set; } = new();
    public UnitTestingOpenXrEyeResolutionSettings OpenXrEyeResolution { get; set; } = new();
    /// <summary>
    /// Optional process-scoped XR_RUNTIME_JSON manifest for OpenXR modes.
    /// Existing XR_RUNTIME_JSON environment values win. MonadoOpenXR auto-detects
    /// common Monado install/build locations when this is unset.
    /// </summary>
    public string? OpenXrRuntimeJson { get; set; }
}
