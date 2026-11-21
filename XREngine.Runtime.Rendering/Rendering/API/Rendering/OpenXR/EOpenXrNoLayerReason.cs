namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Why an OpenXR frame ended with no projection layer. The smoke diagnostics count each
/// reason, so a run can tell runtime-requested skips from engine render failures.
/// </summary>
public enum EOpenXrNoLayerReason
{
    /// <summary>No specific reason was recorded for the no-layer frame.</summary>
    Unknown,
    /// <summary>xrWaitFrame returned shouldRender = false, so the runtime asked for no layers.</summary>
    RuntimeShouldNotRender,
    /// <summary>The visibility collect found no supported view render mode for the backend.</summary>
    CollectUnsupportedViewRenderMode,
    /// <summary>The render thread found no supported view render mode for the backend.</summary>
    UnsupportedViewRenderMode,
    /// <summary>The eye pipeline selection was rejected or its output was still pending.</summary>
    PipelineSelectionRejected,
    /// <summary>The graphics binding did not render and publish every eye view.</summary>
    EyeRenderFailed,
    /// <summary>The frame was aborted after xrBeginFrame, for example because view location failed.</summary>
    FrameAborted,
}
