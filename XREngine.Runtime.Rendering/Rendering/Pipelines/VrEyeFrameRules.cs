namespace XREngine.Rendering;

/// <summary>
/// Decides which optional rendering features run in a VR eye frame. The Default, Advanced and
/// RVC pipelines read these rules instead of a pipeline-local eye gate, so one
/// <see cref="VrEyeFeatureProfile"/> (<see cref="RuntimeEngine.Rendering.EngineSettings.VrEyeFeatures"/>)
/// controls every eye frame and never affects the desktop.
/// </summary>
/// <remarks>
/// <para>
/// An eye frame is a stereo pass, or a mono render whose viewport is a VR eye: its camera is an
/// eye camera (<see cref="XRCamera.StereoEyeLeft"/> is set, which covers OpenXR and OpenVR
/// eyes), or it writes an external swapchain image (an OpenXR per-eye target). Scene captures
/// and light probe captures are never eye frames.
/// </para>
/// <para>
/// A resource key must give the same answer inside and outside the eye render, so it passes
/// its viewport and the pipeline's stereo flag. A render-time condition passes nothing and
/// reads the viewport of the render that runs on this thread. Without a viewport (command
/// chain or resource generation), a query is not an eye frame, so the generated graph keeps
/// every feature and the render-time conditions decide.
/// </para>
/// </remarks>
internal static class VrEyeFrameRules
{
    private static VrEyeFeatureProfile Profile => RuntimeEngine.Rendering.Settings.VrEyeFeatures;

    /// <summary>
    /// True when the render that runs on this thread is a VR eye frame. With
    /// <paramref name="viewport"/>, it tests that viewport instead of the active one;
    /// <paramref name="stereoPipeline"/> marks a stereo pipeline, whose frames are always eye frames.
    /// </summary>
    internal static bool IsEyeFrame(XRViewport? viewport = null, bool stereoPipeline = false)
    {
        if (RuntimeEngine.Rendering.State.IsSceneCapturePass || RuntimeEngine.Rendering.State.IsLightProbePass)
            return false;
        if (stereoPipeline || (viewport is null && RuntimeEngine.Rendering.State.IsStereoPass))
            return true;

        XRViewport? target = viewport ??
            RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport ??
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport;
        return target is not null &&
            (target.RendersToExternalSwapchainTarget || target.ActiveCamera?.StereoEyeLeft is not null);
    }

    /// <summary>
    /// True when the render is an OpenXR per-eye frame that writes an external swapchain image
    /// (a mono eye render, not a stereo pass). Output rules that depend on that target use it:
    /// the final output goes straight to the runtime image, so vendor upscalers (which own their
    /// own output target) do not run there, and an eye target without an exposure partner holds
    /// its exposure. It is not a feature switch; <see cref="VrEyeFeatureProfile"/> holds those.
    /// </summary>
    internal static bool IsExternalEyeTargetFrame(XRViewport? viewport = null, bool stereoPipeline = false)
    {
        if (stereoPipeline || (viewport is null && RuntimeEngine.Rendering.State.IsStereoPass))
            return false;
        if (RuntimeEngine.Rendering.State.IsSceneCapturePass || RuntimeEngine.Rendering.State.IsLightProbePass)
            return false;

        XRViewport? target = viewport ??
            RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport ??
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport;
        return target?.RendersToExternalSwapchainTarget == true;
    }

    /// <summary>True unless an eye frame runs with the profile's ambient occlusion off.</summary>
    internal static bool AllowsAmbientOcclusion(XRViewport? viewport = null, bool stereoPipeline = false)
        => Profile.AmbientOcclusion || !IsEyeFrame(viewport, stereoPipeline);

    /// <summary>True unless an eye frame runs with the profile's fog and aerial perspective off.</summary>
    internal static bool AllowsFogAndAerialPerspective(XRViewport? viewport = null, bool stereoPipeline = false)
        => Profile.FogAndAerialPerspective || !IsEyeFrame(viewport, stereoPipeline);

    /// <summary>True unless an eye frame runs with the profile's order-independent transparency off.</summary>
    internal static bool AllowsOrderIndependentTransparency(XRViewport? viewport = null, bool stereoPipeline = false)
        => Profile.OrderIndependentTransparency || !IsEyeFrame(viewport, stereoPipeline);

    /// <summary>True unless an eye frame runs with the profile's depth of field off.</summary>
    internal static bool AllowsDepthOfField(XRViewport? viewport = null, bool stereoPipeline = false)
        => Profile.DepthOfField || !IsEyeFrame(viewport, stereoPipeline);

    /// <summary>True unless an eye frame runs with the profile's motion blur off.</summary>
    internal static bool AllowsMotionBlur(XRViewport? viewport = null, bool stereoPipeline = false)
        => Profile.MotionBlur || !IsEyeFrame(viewport, stereoPipeline);

    /// <summary>True unless an eye frame runs with the profile's deferred MSAA off.</summary>
    internal static bool AllowsDeferredMsaa(XRViewport? viewport = null, bool stereoPipeline = false)
        => Profile.DeferredMsaa || !IsEyeFrame(viewport, stereoPipeline);

    /// <summary>True unless an eye frame runs with the profile's Forward+ light culling off.</summary>
    internal static bool AllowsForwardPlusLightCulling(XRViewport? viewport = null, bool stereoPipeline = false)
        => Profile.ForwardPlusLightCulling || !IsEyeFrame(viewport, stereoPipeline);
}
