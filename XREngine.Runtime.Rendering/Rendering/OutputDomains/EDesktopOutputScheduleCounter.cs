namespace XREngine.Rendering.OutputDomains;

/// <summary>
/// Names one cumulative counter of <see cref="DesktopOutputScheduleTelemetry"/>: the collects that
/// render or retain the desktop shadow source of a VR session with an independent desktop window,
/// the directional cascade work of the desktop and HMD sources in these collects, and the two
/// mismatch directions between the collect's decision and the desktop render.
/// </summary>
public enum EDesktopOutputScheduleCounter : byte
{
    /// <summary>Collects in which at least one desktop viewport of the world renders its scene.</summary>
    DesktopSceneDueCollects = 0,

    /// <summary>Collects in which no desktop viewport of the world renders its scene, so the lights retained the desktop source.</summary>
    DesktopSourceRetainedCollects,

    /// <summary>Desktop cascade requests that kept their resident tile without a render because the lights retained the desktop source.</summary>
    DesktopCascadeRequestsRetained,

    /// <summary>Camera-move cascade refresh grants that the desktop source received.</summary>
    CascadeGrantsDesktop,

    /// <summary>Camera-move cascade refresh grants that the HMD source received in collects that render a desktop scene.</summary>
    CascadeGrantsHmdDesktopDue,

    /// <summary>Camera-move cascade refresh grants that the HMD source received in collects that retained the desktop source.</summary>
    CascadeGrantsHmdDesktopRetained,

    /// <summary>
    /// Desktop scene renders of a frame whose collect retained the desktop source. Such a render
    /// reads stale desktop cascades. The value must stay 0.
    /// </summary>
    DesktopSceneRenderedWhileRetained,

    /// <summary>
    /// Desktop viewport frames that the collect made due, but whose scene did not render (for
    /// example a breaker hold, a busy swapchain or a minimized window). The desktop shadow work
    /// of these frames was wasted.
    /// </summary>
    DesktopSceneDueNotRendered,

    /// <summary>The number of counter values. Not a counter.</summary>
    Count,
}
