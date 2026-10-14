namespace XREngine.Rendering;

/// <summary>
/// Cumulative stage times of the collect phase and of the serial swap, from <see cref="CollectVisibleStageTimings.Capture"/>.
/// All values are process-wide sums since the process started. Capture twice and use the differences.
/// </summary>
/// <param name="CollectCalls">The number of collect dispatches that finished.</param>
/// <param name="SwapCalls">The number of serial swaps that finished in the threaded loop. The caller-thread loop does not record the swap.</param>
/// <param name="CollectTotalMilliseconds">
/// The collect-phase time. It uses the same start and end timestamps as the collect-phase total of the frame lifecycle.
/// </param>
/// <param name="PreCollectEventMilliseconds">The time of the pre-collect event subscribers.</param>
/// <param name="CollectEventMilliseconds">The time of the collect event subscribers: the lights, the viewports and the other collectors.</param>
/// <param name="PostCollectEventMilliseconds">The time of the post-collect event subscribers.</param>
/// <param name="CollectAllocatedBytes">The managed bytes that the collecting thread allocated in the collect phase.</param>
/// <param name="SwapTotalMilliseconds">
/// The serial swap time. It uses the same start and end timestamps as the swap-phase total of the frame lifecycle.
/// </param>
/// <param name="SwapAllocatedBytes">The managed bytes that the collect thread allocated in the serial swap.</param>
/// <param name="PreCollect">The split of the world pre-collect work.</param>
/// <param name="Lights">The split of the light collection.</param>
/// <param name="MainView">The automatic viewport collections.</param>
/// <param name="PrimaryShadow">The primary directional shadow collections.</param>
/// <param name="Cascades">The directional cascade collections.</param>
/// <param name="LocalShadows">The spot and point shadow collections.</param>
/// <param name="OtherViews">
/// The path times of collections outside a named view, for example XR eye, capture or UI collections.
/// Its call count and total time stay at zero.
/// </param>
/// <param name="Swap">The split of the serial swap.</param>
/// <param name="SwapLights">The split of the light swap in the world swap.</param>
/// <param name="Handoff">The frame handoff counters between the render thread and the collect thread.</param>
public readonly record struct CollectVisibleStageTimingSnapshot(
    long CollectCalls,
    long SwapCalls,
    double CollectTotalMilliseconds,
    double PreCollectEventMilliseconds,
    double CollectEventMilliseconds,
    double PostCollectEventMilliseconds,
    long CollectAllocatedBytes,
    double SwapTotalMilliseconds,
    long SwapAllocatedBytes,
    CollectVisiblePreCollectStageTimings PreCollect,
    CollectVisibleLightStageTimings Lights,
    CollectVisibleViewStageTimings MainView,
    CollectVisibleViewStageTimings PrimaryShadow,
    CollectVisibleViewStageTimings Cascades,
    CollectVisibleViewStageTimings LocalShadows,
    CollectVisibleViewStageTimings OtherViews,
    CollectVisibleSwapStageTimings Swap,
    CollectVisibleSwapLightStageTimings SwapLights,
    CollectVisibleHandoffTimings Handoff);
