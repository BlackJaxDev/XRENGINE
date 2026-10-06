namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    /// <summary>
    /// Confirms that every active eye viewport runs exactly the selected VR
    /// pipeline family, and that the family can produce output this frame.
    /// The caller submits no projection layer when this returns false. Runs on
    /// the render thread, which owns the Advanced output binding.
    /// </summary>
    private bool TryValidateOpenXrEyePipelineSelection(out string diagnostic, out bool pendingResources)
    {
        pendingResources = false;
        EVrRenderPipeline family = RuntimeRenderingHostServices.Presentation.VrRenderPipeline;
        ERvcPipelineMode rvcMode = RuntimeEngine.Rendering.Settings.RvcPipelineMode;

        if (Volatile.Read(ref _pendingXrFrameUsesTrueSinglePassStereo) != 0)
            return TryValidateOpenXrEyeViewport(_openXrStereoViewport, "stereo", stereo: true, family, rvcMode, out diagnostic, ref pendingResources);

        return TryValidateOpenXrEyeViewport(_openXrLeftViewport, "left", stereo: false, family, rvcMode, out diagnostic, ref pendingResources) &&
               TryValidateOpenXrEyeViewport(_openXrRightViewport, "right", stereo: false, family, rvcMode, out diagnostic, ref pendingResources);
    }

    private static bool TryValidateOpenXrEyeViewport(
        XRViewport? viewport,
        string eyeName,
        bool stereo,
        EVrRenderPipeline family,
        ERvcPipelineMode rvcMode,
        out string diagnostic,
        ref bool pendingResources)
    {
        if (viewport is null)
        {
            diagnostic = $"The {eyeName} eye viewport does not exist.";
            return false;
        }

        RenderPipeline? pipeline = viewport.RenderPipeline;
        if (!OpenXrEyeRenderPipelineFactory.Matches(pipeline, stereo, family, rvcMode))
        {
            diagnostic =
                $"The {eyeName} eye runs {OpenXrEyeRenderPipelineFactory.Describe(pipeline)}, " +
                $"but the selection requires VR.RenderPipeline={family} (stereo={stereo}, RvcPipelineMode={rvcMode}).";
            return false;
        }

        switch (pipeline)
        {
            case RvcRenderPipeline rvcPipeline:
                RvcPipelineResolution resolution = rvcPipeline.LastRvcResolution;
                if (resolution.FallbackReason != ERvcFallbackReason.None)
                {
                    diagnostic =
                        $"RVC cannot run RvcPipelineMode={resolution.RequestedMode} on the {eyeName} eye " +
                        $"(reason={resolution.FallbackReason}). {resolution.Diagnostic} " +
                        "The Forward+ oracle fallback is forbidden for an explicit RVC selection.";
                    return false;
                }
                break;

            case AdvancedRenderPipeline:
                AdvancedRenderPipelineOutputBinding binding = viewport.RenderPipelineInstance.AdvancedOutputBinding;
                if (!binding.IsBound)
                {
                    // The binding refresh is queued from other threads. Apply
                    // it here once, as the Advanced preparation stage does.
                    RuntimeEngine.Rendering.RefreshRenderPipelineOutputBinding(viewport);
                    binding = viewport.RenderPipelineInstance.AdvancedOutputBinding;
                }
                if (!binding.IsBound)
                {
                    pendingResources = binding.State == EAdvancedRenderPipelineOutputBindingState.PendingResources;
                    diagnostic =
                        $"The Advanced {eyeName} eye output is not bound (state={binding.State}). " +
                        $"{binding.FailureReason ?? "No reason was recorded."}";
                    return false;
                }
                break;
        }

        diagnostic = string.Empty;
        return true;
    }

    /// <summary>
    /// Reports a frame that the eye pipeline gate rejected. Pending Advanced
    /// resources are expected while the output reservation starts, so they are
    /// not recorded as a smoke failure.
    /// </summary>
    private void ReportOpenXrEyePipelineSelectionRejected(string diagnostic, bool pendingResources)
    {
        EVrRenderPipeline family = RuntimeRenderingHostServices.Presentation.VrRenderPipeline;
        EVrViewRenderMode mode = RuntimeRenderingHostServices.Presentation.VrViewRenderMode;
        if (pendingResources)
        {
            Debug.RenderingEvery(
                $"OpenXR.PipelineSelection.Pending.{mode}.{family}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] VR pipeline output pending. VR.ViewRenderMode={0} VR.RenderPipeline={1}. {2} The frame is submitted without projection layers.",
                mode,
                family,
                diagnostic);
            return;
        }

        Debug.RenderingWarningEvery(
            $"OpenXR.PipelineSelection.Rejected.{mode}.{family}",
            TimeSpan.FromSeconds(5),
            "[OpenXR] VR pipeline selection rejected. VR.ViewRenderMode={0} VR.RenderPipeline={1}. {2} The frame is submitted without projection layers.",
            mode,
            family,
            diagnostic);
        RecordSmokeFailureOnce(
            $"VR pipeline selection rejected. VR.ViewRenderMode={mode} VR.RenderPipeline={family}. {diagnostic}");
    }
}
