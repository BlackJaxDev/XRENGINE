using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>
    /// Minimum time between two warnings that TSR is off for an external eye target.
    /// </summary>
    private static readonly long TemporalHistoryOffWarningIntervalTicks =
        System.Diagnostics.Stopwatch.Frequency * 10L;

    // Stereo flag of the last frame profile. A viewport resize outside a render reuses it.
    private bool _lastFrameProfileStereoPass;
    // Stopwatch timestamp of the last "TSR off for this eye" warning; 0 when none was sent.
    private long _lastTemporalHistoryOffWarningTimestamp;

    /// <summary>
    /// Captures the output-dependent render profile used by both resource preparation and command recording.
    /// A resource generation must never be prepared against a profile that differs from its first command package.
    /// </summary>
    /// <param name="camera">The scene camera of the view.</param>
    /// <param name="stereoRightEyeCamera">The right eye camera of a stereo pass, or null.</param>
    /// <param name="viewport">The viewport that owns the render extent, or null.</param>
    /// <param name="stereoPass">True when this render draws both eyes in one stereo pass.</param>
    private void ApplyCurrentFrameProfile(
        XRCamera? camera,
        XRCamera? stereoRightEyeCamera,
        XRViewport? viewport,
        bool stereoPass)
    {
        IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
        LastSceneCamera = camera;
        LastRenderingCamera = camera ?? stereoRightEyeCamera;
        LastWindowViewport = viewport;
        FinalOutput = AbstractRenderer.Current?.CurrentFrameOutput;
        _lastFrameProfileStereoPass = stereoPass;

        XRCamera? effectiveAntiAliasingCamera = camera ?? stereoRightEyeCamera;
        EAntiAliasingMode effectiveAntiAliasingMode =
            effectiveAntiAliasingCamera?.AntiAliasingModeOverride
            ?? frameTiming.DefaultAntiAliasingMode;
        EffectiveOutputHDRThisFrame = camera?.OutputHDROverride
            ?? (camera is null ? stereoRightEyeCamera?.OutputHDROverride : null)
            ?? frameTiming.DefaultOutputHDR;
        // DLSS Super Resolution supplies its own anti-aliasing. Keep the authored
        // camera/settings mode intact so it resumes when DLSS is disabled, but
        // prevent the frame profile from allocating or executing engine AA.
        bool useDlssSuperResolution = RuntimeEngine.EffectiveSettings.EnableNvidiaDlss
            && Pipeline is (DefaultRenderPipeline or AdvancedRenderPipeline)
            && viewport?.RendersToExternalSwapchainTarget != true
            && AbstractRenderer.Current?.IsRenderingExternalSwapchainTarget != true;
        EffectiveAntiAliasingModeThisFrame = useDlssSuperResolution
            ? EAntiAliasingMode.None
            : effectiveAntiAliasingMode;
        EffectiveAntiAliasingFilterModeThisFrame = frameTiming.DefaultAntiAliasingFilterMode;
        EffectiveMsaaSampleCountThisFrame = Math.Max(1u,
            FinalOutput?.SceneSampleCountOverride ??
            effectiveAntiAliasingCamera?.MsaaSampleCountOverride ??
            frameTiming.DefaultMsaaSampleCount);

        // TSR needs temporal history. When the history policy of this view turns history off,
        // the passes skip TSR, so the view must also keep its full internal size.
        bool temporalHistoryAllowed = IsTemporalHistoryAllowedForProfile(effectiveAntiAliasingMode, stereoPass);
        EffectiveTsrRenderScaleThisFrame = !useDlssSuperResolution &&
            effectiveAntiAliasingMode == EAntiAliasingMode.Tsr &&
            temporalHistoryAllowed
            ? Math.Clamp(
                effectiveAntiAliasingCamera?.TsrRenderScaleOverride ?? frameTiming.DefaultTsrRenderScale,
                0.5f,
                1.0f)
            : null;
        ForwardContactPrePassAvailableThisFrame = false;

        if (viewport is null)
            return;

        ApplyAutomaticInternalResolutionPolicy(
            viewport,
            effectiveAntiAliasingCamera,
            effectiveAntiAliasingMode,
            temporalHistoryAllowed,
            // Vendor upscalers (DLSS, XeSS) run only on mono views that do not write an
            // external swapchain image, so stereo and OpenXR eyes use the TSR scale.
            allowVendorUpscale: !stereoPass &&
                !viewport.RendersToExternalSwapchainTarget &&
                AbstractRenderer.Current?.IsRenderingExternalSwapchainTarget != true);

        // The bridge belongs to the viewport's outer render. A nested screen-space
        // UI pipeline must not disable or recreate it while the scene generation
        // is converging.
        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null &&
            !RuntimeEngine.Rendering.State.IsSceneCapturePass &&
            !RuntimeEngine.Rendering.State.IsLightProbePass)
            RuntimeRenderingHostServices.BackendInterop.PrepareUpscaleBridgeForFrame(viewport, this);
    }

    /// <summary>
    /// Reconciles the pipeline-specific AA/upscale resolution hint after the viewport's own
    /// Full/Scale/Manual camera policy. Held native resize intentionally preserves the last
    /// applied scale and generation until the release commit.
    /// </summary>
    internal void ApplyAutomaticInternalResolutionPolicy(XRViewport viewport)
    {
        // An instance that its owner released holds no pipeline on purpose. Reading Pipeline
        // here would find none and fail, so the viewport keeps its base internal size.
        if (IsPipelineReleasedByOwner && PipelineIfAssigned is null)
        {
            _appliedInternalResolutionScale = null;
            return;
        }

        XRCamera? camera = viewport.ActiveCamera;
        EAntiAliasingMode antiAliasingMode = camera?.AntiAliasingModeOverride
            ?? RuntimeRenderingHostServices.FrameTiming.DefaultAntiAliasingMode;
        ApplyAutomaticInternalResolutionPolicy(
            viewport,
            camera,
            antiAliasingMode,
            IsTemporalHistoryAllowedForProfile(antiAliasingMode, _lastFrameProfileStereoPass),
            allowVendorUpscale: !_lastFrameProfileStereoPass && !viewport.RendersToExternalSwapchainTarget);
    }

    private void ApplyAutomaticInternalResolutionPolicy(
        XRViewport viewport,
        XRCamera? camera,
        EAntiAliasingMode antiAliasingMode,
        bool temporalHistoryAllowed,
        bool allowVendorUpscale)
    {
        if (!viewport.AllowAutomaticInternalResolution)
        {
            _appliedInternalResolutionScale = null;
            return;
        }

        if (ShouldDeferResourceGenerationForInteractiveWindowResize(viewport))
            return;

        RenderPipeline pipeline = Pipeline ?? throw new InvalidOperationException(
            "A render pipeline is required before applying an internal-resolution policy.");
        // A TSR scale without TSR history would only upscale a smaller image, so a view
        // without history keeps its base internal size.
        float? requestedScale = antiAliasingMode == EAntiAliasingMode.Tsr && !temporalHistoryAllowed
            ? null
            : pipeline.GetRequestedInternalResolutionForCamera(camera, antiAliasingMode, allowVendorUpscale);
        if (requestedScale.HasValue)
        {
            float scale = Math.Clamp(requestedScale.Value, 0.25f, 1.25f);
            int expectedWidth = Math.Max(1, (int)(scale * viewport.Width));
            int expectedHeight = Math.Max(1, (int)(scale * viewport.Height));
            if (_appliedInternalResolutionScale == scale &&
                viewport.InternalWidth == expectedWidth &&
                viewport.InternalHeight == expectedHeight)
            {
                return;
            }

            _appliedInternalResolutionScale = scale;
            viewport.SetInternalResolution(expectedWidth, expectedHeight, correctAspect: false);
            return;
        }

        if (!_appliedInternalResolutionScale.HasValue)
            return;

        _appliedInternalResolutionScale = null;
        viewport.ApplyBaseInternalResolutionPolicy();
    }

    /// <summary>
    /// Resolves the temporal history policy for this instance with an explicit stereo flag.
    /// The frame profile runs before the render makes this instance the current pipeline, so
    /// the policy cannot read the current pipeline or its stereo state. Only temporal modes need
    /// the answer; other modes return true without resolving the policy.
    /// </summary>
    private bool IsTemporalHistoryAllowedForProfile(EAntiAliasingMode antiAliasingMode, bool stereoPass)
    {
        if (antiAliasingMode is not (EAntiAliasingMode.Tsr or EAntiAliasingMode.Taa))
            return true;

        if (VPRC_TemporalAccumulationPass.TryUseHistoryBasedVrEffects(
                this,
                stereoPass,
                out EVrTemporalHistoryPolicy policy,
                out string reason))
        {
            return true;
        }

        WarnTemporalHistoryOffForExternalEye(antiAliasingMode, policy, reason);
        return false;
    }

    /// <summary>
    /// Writes a rate-limited warning when an eye that renders to an external swapchain image
    /// requests TSR or TAA but its history policy turns history off. The eye then renders at
    /// its full size without temporal anti-aliasing. The timestamp check runs first, so a frame
    /// inside the interval allocates nothing.
    /// </summary>
    private void WarnTemporalHistoryOffForExternalEye(
        EAntiAliasingMode antiAliasingMode,
        EVrTemporalHistoryPolicy policy,
        string reason)
    {
        if (policy is not (EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain
            or EVrTemporalHistoryPolicy.DisabledPerEyeSwapchain))
        {
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastTemporalHistoryOffWarningTimestamp != 0L &&
            now - _lastTemporalHistoryOffWarningTimestamp < TemporalHistoryOffWarningIntervalTicks)
        {
            return;
        }

        _lastTemporalHistoryOffWarningTimestamp = now;
        Debug.RenderingWarning(
            "[TemporalHistory] {0} is off for the per-eye VR view of pipeline '{1}' (instance {2}): {3} ({4}). " +
            "The eye renders at full size without temporal anti-aliasing. Per-eye temporal history is implemented " +
            "only for a bound Advanced OpenXR eye on Vulkan.",
            antiAliasingMode,
            Pipeline?.DebugName ?? "<none>",
            InstanceId,
            reason,
            policy);
    }
}
