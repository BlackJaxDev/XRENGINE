namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>
    /// Captures the output-dependent render profile used by both resource preparation and command recording.
    /// A resource generation must never be prepared against a profile that differs from its first command package.
    /// </summary>
    private void ApplyCurrentFrameProfile(
        XRCamera? camera,
        XRCamera? stereoRightEyeCamera,
        XRViewport? viewport)
    {
        IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
        LastSceneCamera = camera;
        LastRenderingCamera = camera ?? stereoRightEyeCamera;
        LastWindowViewport = viewport;
        FinalOutput = AbstractRenderer.Current?.CurrentFrameOutput;

        XRCamera? effectiveAntiAliasingCamera = camera ?? stereoRightEyeCamera;
        EAntiAliasingMode effectiveAntiAliasingMode =
            effectiveAntiAliasingCamera?.AntiAliasingModeOverride
            ?? frameTiming.DefaultAntiAliasingMode;
        EffectiveOutputHDRThisFrame = camera?.OutputHDROverride
            ?? (camera is null ? stereoRightEyeCamera?.OutputHDROverride : null)
            ?? frameTiming.DefaultOutputHDR;
        EffectiveAntiAliasingModeThisFrame = effectiveAntiAliasingMode;
        EffectiveMsaaSampleCountThisFrame = Math.Max(1u,
            FinalOutput?.Properties.SampleCount ??
            effectiveAntiAliasingCamera?.MsaaSampleCountOverride ??
            frameTiming.DefaultMsaaSampleCount);
        EffectiveTsrRenderScaleThisFrame = effectiveAntiAliasingMode == EAntiAliasingMode.Tsr
            ? Math.Clamp(
                effectiveAntiAliasingCamera?.TsrRenderScaleOverride ?? frameTiming.DefaultTsrRenderScale,
                0.5f,
                1.0f)
            : null;
        ForwardContactPrePassAvailableThisFrame = false;

        if (viewport is null)
            return;

        RenderPipeline pipeline = Pipeline ?? throw new InvalidOperationException(
            "A render pipeline is required before applying a frame resource profile.");
        if (viewport.AllowAutomaticInternalResolution &&
            !ShouldDeferResourceGenerationForInteractiveWindowResize(viewport))
        {
            float? requestedScale = pipeline.GetRequestedInternalResolutionForCamera(
                effectiveAntiAliasingCamera,
                effectiveAntiAliasingMode);
            if (requestedScale.HasValue)
            {
                float scale = Math.Clamp(requestedScale.Value, 0.25f, 1.25f);
                int expectedWidth = Math.Max(1, (int)(scale * viewport.Width));
                int expectedHeight = Math.Max(1, (int)(scale * viewport.Height));
                if (_appliedInternalResolutionScale != scale ||
                    viewport.InternalWidth != expectedWidth ||
                    viewport.InternalHeight != expectedHeight)
                {
                    _appliedInternalResolutionScale = scale;
                    viewport.SetInternalResolution(expectedWidth, expectedHeight, correctAspect: false);
                }
            }
            else if (_appliedInternalResolutionScale.HasValue)
            {
                _appliedInternalResolutionScale = null;
                viewport.SetInternalResolution(viewport.Width, viewport.Height, true);
            }
        }
        else
        {
            _appliedInternalResolutionScale = null;
        }

        if (!RuntimeEngine.Rendering.State.IsSceneCapturePass && !RuntimeEngine.Rendering.State.IsLightProbePass)
            RuntimeRenderingHostServices.BackendInterop.PrepareUpscaleBridgeForFrame(viewport, this);
    }
}
