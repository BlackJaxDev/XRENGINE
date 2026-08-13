namespace XREngine.Rendering;

/// <summary>
/// Terminal result for one native modal-loop repaint request. Every callback
/// settles immediately as a fresh submit, scaled stale reuse, or typed defer.
/// </summary>
public readonly record struct InteractiveResizeDispatchResult(
    EInteractiveResizeDispatchOutcome Outcome,
    EInteractiveResizeDispatchReason Reason,
    ulong PresentFrameId,
    long ElapsedStopwatchTicks)
{
    public bool Presented => Outcome is
        EInteractiveResizeDispatchOutcome.Submitted or
        EInteractiveResizeDispatchOutcome.PresentedScaledStale;

    public static InteractiveResizeDispatchResult Deferred(
        EInteractiveResizeDispatchReason reason,
        ulong presentFrameId,
        long elapsedStopwatchTicks = 0L)
        => new(
            EInteractiveResizeDispatchOutcome.Deferred,
            reason,
            presentFrameId,
            elapsedStopwatchTicks);
}
