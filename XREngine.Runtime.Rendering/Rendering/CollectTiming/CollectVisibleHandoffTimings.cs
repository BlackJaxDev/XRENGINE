namespace XREngine.Rendering;

/// <summary>
/// Cumulative frame handoff counters between the render thread and the collect thread.
/// </summary>
/// <remarks>
/// The threaded loop starts the serial swap when two conditions are true: the collect phase is complete, and the
/// render thread has released the collect thread. Thus the slower of the two sets the frame interval, plus the
/// serial swap. These counters show which side was slower.
/// </remarks>
/// <param name="RenderUntilCollectReleaseCalls">
/// The number of render dispatches that released the collect thread after the dispatch recorded its start.
/// </param>
/// <param name="RenderUntilCollectReleaseMilliseconds">
/// The time from the start of a render dispatch to the first release of the collect thread in that frame. A backend
/// can release the collect thread after its queue submission, before the render dispatch ends. Otherwise the release
/// comes after the render dispatch.
/// </param>
/// <param name="CollectIterations">The number of collect iterations that reached the wait for the render thread.</param>
/// <param name="CollectFinishedAfterRelease">
/// The number of collect iterations that found the render release already set at the wait. In these iterations the
/// collect phase was the slower side, so the collect thread did not wait.
/// </param>
public readonly record struct CollectVisibleHandoffTimings(
    long RenderUntilCollectReleaseCalls,
    double RenderUntilCollectReleaseMilliseconds,
    long CollectIterations,
    long CollectFinishedAfterRelease);
