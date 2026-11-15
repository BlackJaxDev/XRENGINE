namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Paces the engine collect loop to OpenXR frames, so that the loop collects and publishes one
/// visibility generation for each XR frame. The collect thread calls both members; the OpenXR
/// runtime implements them.
/// </summary>
/// <remarks>
/// Without pacing, the collect loop publishes again each time the render thread finishes a
/// dispatch, also while the render thread only waits for the next XR frame. An XR frame then
/// waits for the collect that is in progress when <c>xrWaitFrame</c> returns. With pacing, the
/// collect for frame N+1 starts when the render thread ends frame N, so the collect and the swap
/// run while the runtime blocks in <c>xrWaitFrame</c>. The publication then waits for that
/// <c>xrWaitFrame</c>, so it carries the located views of frame N+1.
/// </remarks>
public interface IOpenXrCollectPacing
{
    /// <summary>
    /// The runtime's predicted display period of the last XR frame, in stopwatch ticks, or 0 when
    /// unknown. The engine timer counts the budget periods of a paced collect with it
    /// (<see cref="CollectBudgetPeriods"/>).
    /// </summary>
    long DisplayPeriodTicks { get; }

    /// <summary>True when the current collect started under XR collect pacing. Collect thread.</summary>
    bool IsCurrentCollectPaced { get; }

    /// <summary>
    /// Slack in milliseconds that the independent desktop admission uses when the collect is paced,
    /// or -1 when unknown. It estimates the window in which an admitted desktop frame renders (after
    /// the next <c>xrEndFrame</c>) from completed frames, instead of the time left at the collect
    /// start. Collect thread.
    /// </summary>
    double PacedDesktopSlackMs { get; }

    /// <summary>The render thread starts to render the desktop viewports of a window. Records the lead after <c>xrEndFrame</c>.</summary>
    void NoteDesktopRenderStart();

    /// <summary>
    /// Collect-start gate. When the last publication carried an XR frame, waits until the render
    /// thread ends that frame (<c>xrEndFrame</c>). When the next collect needs the located views
    /// of the next frame (no speculative stereo collect), it also waits for that frame's
    /// <c>xrWaitFrame</c>. Each wait is bounded to about two XR periods.
    /// </summary>
    EOpenXrCollectPacingWait WaitForCollectRelease();

    /// <summary>
    /// Publication gate. Runs after the swap and before the visibility generation is published.
    /// When a speculative stereo collect waits for its XR frame, waits (bounded) for
    /// <c>xrWaitFrame</c> of that frame and then publishes the eye commands for it.
    /// </summary>
    EOpenXrCollectPacingWait CompletePacedFramePublication();
}
