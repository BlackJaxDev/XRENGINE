using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Required desktop-window, VR, OpenXR, and mirror presentation policy and operations.
/// </summary>
public interface IRuntimeRenderPresentationServices : IRuntimeRenderFrameTimingServices
{
    /// <summary>
    /// Gets whether editor scene panel presentation is enabled for render windows.
    /// </summary>
    bool IsWindowScenePanelPresentationEnabled { get; }

    /// <summary>
    /// Gets the host default strategy for interactive window resize rendering.
    /// </summary>
    EInteractiveWindowResizeStrategy InteractiveResizeStrategy { get; }

    /// <summary>
    /// Gets the scene panel resize debounce interval in milliseconds.
    /// </summary>
    int ScenePanelResizeDebounceMs { get; }

    /// <summary>
    /// Gets whether windows should ignore panel regions and render to the full viewport.
    /// </summary>
    bool ForceFullViewport { get; }


    /// <summary>
    /// Gets whether desktop render windows should continue rendering while XR presentation is active.
    /// </summary>
    bool RenderWindowsWhileInVR { get; }

    /// <summary>
    /// Gets whether the OpenXR Vulkan path may use parallel eye rendering/recording when supported.
    /// </summary>
    bool EnableOpenXrVulkanParallelRendering { get; }

    /// <summary>
    /// Gets whether startup or the active runtime is using the OpenXR path.
    /// </summary>
    bool IsOpenXrRuntimeRequested { get; }

    /// <summary>
    /// Gets the requested VR view rendering strategy.
    /// </summary>
    EVrViewRenderMode VrViewRenderMode { get; }

    /// <summary>
    /// Gets the active VR desktop mirror policy.
    /// </summary>
    EVrMirrorMode VrMirrorMode => RuntimeRenderingHostServiceDefaults.VrMirrorMode;

    /// <summary>
    /// Gets the target output rate for a VR view. A value of zero matches the XR runtime cadence.
    /// </summary>
    float GetVrOutputTargetRateHz(EVrOutputViewKind viewKind)
        => RuntimeRenderingHostServiceDefaults.VrOutputTargetRateHz;

    /// <summary>
    /// Gets whether desktop-facing VR outputs may be skipped to protect the active XR budget band.
    /// </summary>
    bool VrDesktopAutoSkipWhenOverBudget => RuntimeRenderingHostServiceDefaults.VrDesktopAutoSkipWhenOverBudget;

    /// <summary>
    /// Evaluates whether an output is due for this frame according to host pacing policy.
    /// </summary>
    FrameOutputPacingDecision EvaluateFrameOutputPacing(
        EVrOutputViewKind viewKind,
        EFrameOutputKind outputKind,
        bool xrCritical)
        => FrameOutputPacingDecision.Due(
            viewKind,
            outputKind,
            CurrentRenderFrameId,
            GetVrOutputTargetRateHz(viewKind));

    /// <summary>Registers an output request in the persistent frame-output DAG.</summary>
    void PlanRenderOutput(in RenderOutputRequest request, bool isDue) { }

    /// <summary>
    /// Records a per-output frame manifest contribution and updates DAG completion state.
    /// </summary>
    void RecordRenderFrameOutput(in FrameOutputTelemetry telemetry) { }

    void RecordRenderFrameOutputWork(in FrameOutputWorkTelemetry telemetry) { }

    /// <summary>
    /// Gets whether VR rendering should configure a foveated multi-view view set.
    /// </summary>
    bool EnableVrFoveatedViewSet { get; }

    ERvcPipelineMode RvcPipelineMode => ERvcPipelineMode.Off;
    bool RvcQuadViewEnabled => false;
    bool RvcOpenXrVisibilityMaskEnabled => false;
    double ResolveRvcViewGpuMilliseconds(int runtimeViewIndex, int runtimeViewCount)
    {
        if (!GpuRenderPipelineTimingsReady || runtimeViewCount <= 0)
            return 0.0;

        double frameMs = GpuRenderPipelineFrameMs;
        if (frameMs <= 0.0 || double.IsNaN(frameMs) || double.IsInfinity(frameMs))
            return 0.0;

        return frameMs / runtimeViewCount;
    }

    EVrFoveationMode VrFoveationMode { get; }
    EVrFoveationQualityPreset VrFoveationQualityPreset { get; }
    bool VrFoveationRequireRequested { get; }
    EOpenXrEyeResolutionPreset OpenXrEyeResolutionPreset { get; }
    float OpenXrEyeResolutionScale { get; }
    uint OpenXrCustomEyeResolutionWidth { get; }
    uint OpenXrCustomEyeResolutionHeight { get; }

    /// <summary>
    /// Gets whether the host is currently in VR mode.
    /// </summary>
    bool IsInVR { get; }

    /// <summary>
    /// Gets whether OpenXR is the active XR runtime path.
    /// </summary>
    bool IsOpenXRActive { get; }

    /// <summary>
    /// Gets whether the desktop mirror should compose from eye textures instead of rendering desktop viewports directly.
    /// </summary>
    bool VrMirrorComposeFromEyeTextures { get; }

    /// <summary>
    /// Gets whether OpenXR eye swapchain output should be copied into preview textures for UI or diagnostics.
    /// </summary>
    bool VrCopyEyePreviewTextures { get; }

    /// <summary>
    /// Gets the normalized UV center of the VR foveation region.
    /// </summary>
    Vector2 VrFoveationCenterUv { get; }

    /// <summary>
    /// Gets the normalized inner radius of the VR foveation full-rate region.
    /// </summary>
    float VrFoveationInnerRadius { get; }

    /// <summary>
    /// Gets the normalized outer radius of the VR foveation falloff region.
    /// </summary>
    float VrFoveationOuterRadius { get; }

    /// <summary>
    /// Gets the shading rates used for inner, middle, and outer VR foveation regions.
    /// </summary>
    Vector3 VrFoveationShadingRates { get; }

    /// <summary>
    /// Gets the visibility margin applied around the VR foveation region.
    /// </summary>
    float VrFoveationVisibilityMargin { get; }

    /// <summary>
    /// Gets whether UI and near-field geometry should force full-resolution foveated rendering.
    /// </summary>
    bool VrFoveationForceFullResForUiAndNearField { get; }

    /// <summary>
    /// Gets the near-field distance threshold, in meters, used for full-resolution foveated rendering.
    /// </summary>
    float VrFoveationFullResNearDistanceMeters { get; }

    bool OpenXrCullWithFrustum { get; }
    bool OpenXrDebugGl { get; }
    bool OpenXrDebugClearOnly { get; }
    bool OpenXrDebugLifecycle { get; }
    bool OpenXrDebugRenderRightThenLeft { get; }
    bool OpenXrPrepareFrameAfterDesktopRender { get; }
    float OpenXrDeadlineSafetyMarginMs { get; }
    float OpenXrPoseTimeOffsetMs { get; }
    OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy { get; }
    float OpenXrCollectVisibleFrustumPaddingDegrees { get; }
    OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy { get; }
    OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy { get; }
    OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode { get; }

    /// <summary>
    /// Ensures a host-owned process-scoped OpenXR runtime service is running before recovery probes continue.
    /// Hosts that do not manage a runtime service can use the default no-op implementation.
    /// </summary>
    bool TryEnsureOpenXrRuntimeService(string reason)
        => RuntimeRenderingHostServices.TryEnsureOpenXrRuntimeService(reason);

    /// <summary>
    /// Attempts to render the host desktop mirror composition into the current target size.
    /// </summary>
    bool TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight);

    /// <summary>
    /// Records left-eye and right-eye draw counts for VR diagnostics.
    /// </summary>
    void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws);
}
