namespace XREngine.Rendering;

/// <summary>Cumulative counters of XR collect pacing, kept by <see cref="XrCollectPacingTelemetry"/>.</summary>
public enum EXrCollectPacingCounter
{
    /// <summary>Collect iterations that started while XR collect pacing was active.</summary>
    PacedCollects,
    /// <summary>
    /// Collect iterations in an OpenXR session with the pacing setting on, while pacing was not
    /// active (no focused session, the runtime asked for no rendering, or the last XR frame had
    /// no eyes). These collects use render pacing.
    /// </summary>
    RenderPacedCollects,
    /// <summary>Collect starts that waited for the render thread to end the XR frame of the last publication.</summary>
    SubmitWaits,
    /// <summary>Submit waits that stopped at the time limit (fallback to render pacing for that collect).</summary>
    SubmitWaitTimeouts,
    /// <summary>Total time of the submit waits, in microseconds.</summary>
    SubmitWaitMicroseconds,
    /// <summary>
    /// Collect starts that also waited for <c>xrWaitFrame</c>, because the next collect is a full
    /// stereo collect with the located views (a speculative collect could not be assigned).
    /// </summary>
    LocatedFrameWaitsBeforeCollect,
    /// <summary>Publications that waited for <c>xrWaitFrame</c> after the swap, to publish a speculative stereo collect.</summary>
    PublicationFrameWaits,
    /// <summary>Publication frame waits (and located-frame waits before a collect) that stopped at the time limit.</summary>
    FrameWaitTimeouts,
    /// <summary>Total time of the publication and located-frame waits, in microseconds.</summary>
    FrameWaitMicroseconds,
    /// <summary>Waits that ended because pacing stopped during the wait (focus loss, session stop, frame without eyes).</summary>
    Deactivations,
    /// <summary>Paced publications that carried a prepared XR frame for the render thread.</summary>
    PublicationsWithXrFrame,
    /// <summary>Paced publications that carried no XR frame (the frame was not ready or its collect failed).</summary>
    PublicationsWithoutXrFrame,
    /// <summary>Number of counters. Not a counter.</summary>
    Count,
}
