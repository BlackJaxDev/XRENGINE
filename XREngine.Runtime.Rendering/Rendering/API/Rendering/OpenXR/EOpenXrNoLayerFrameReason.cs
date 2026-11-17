namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Names the exit through which an OpenXR frame ended without projection layers. Each exit
/// calls <c>xrEndFrame</c> with no layers, so the headset shows no new image for that frame.
/// <see cref="RuntimeEngine.Rendering.Stats.Vr"/> counts each exit separately.
/// </summary>
public enum EOpenXrNoLayerFrameReason : byte
{
    /// <summary>The runtime returned <c>shouldRender = false</c> from <c>xrWaitFrame</c>.</summary>
    FrameSkip,

    /// <summary>The requested view render mode is not supported by the current backend.</summary>
    UnsupportedViewMode,

    /// <summary>The selected eye pipeline is not ready yet (for example, it still compiles).</summary>
    EyePipelinePending,

    /// <summary>The selected eye pipeline was rejected for this view mode or backend.</summary>
    EyePipelineRejected,

    /// <summary>At least one eye did not render or submit (for example, a failed eye batch).</summary>
    EyeRenderFailure,

    /// <summary>The frame threw an exception, and the cleanup ended it without layers.</summary>
    FrameException,

    /// <summary>The pacing owner began the frame but aborted its preparation (view location or pose update failed).</summary>
    PrepareAborted,

    /// <summary>The number of reasons. Not a reason.</summary>
    Count,
}
