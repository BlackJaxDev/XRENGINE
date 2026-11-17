namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Result of one XR collect pacing gate.</summary>
public enum EOpenXrCollectPacingWait
{
    /// <summary>Pacing is off or does not apply now. The gate did not wait.</summary>
    NotPaced,

    /// <summary>The gate had nothing to wait for (the event it waits for already occurred).</summary>
    Ready,

    /// <summary>The gate waited, and the event occurred in the time limit.</summary>
    Waited,

    /// <summary>The gate stopped at its time limit. The loop continues with render pacing for this collect.</summary>
    TimedOut,

    /// <summary>Pacing stopped during the wait (focus loss, session stop, frame without eyes).</summary>
    Deactivated,
    /// <summary>
    /// A two-pass located-frame wait stopped because the independent VR desktop output needed its
    /// next frame. The loop collects now for the desktop, with render pacing for this collect.
    /// </summary>
    DesktopDue,
}
