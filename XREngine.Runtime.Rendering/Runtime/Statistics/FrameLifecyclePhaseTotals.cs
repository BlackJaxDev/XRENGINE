namespace XREngine;

/// <summary>
/// Allocation-free snapshot of the cumulative frame-loop phase totals.
/// A measurement window subtracts the snapshot taken at its start; the totals
/// are never reset and do not depend on render statistics tracking or the code profiler.
/// </summary>
/// <param name="Update">Update-thread iterations of <c>PreUpdate</c>, <c>Update</c>, and <c>PostUpdate</c>.</param>
/// <param name="Collect">Collect-visible dispatches.</param>
/// <param name="Swap">The collect thread's serial swap section: swap jobs, buffer swap, and generation publication.</param>
/// <param name="Render">Render-thread frame callbacks.</param>
/// <param name="CollectWaitForRender">Time the collect thread waited for the render thread.</param>
/// <param name="RenderWaitForCollect">
/// Time the render thread waited for a visibility publication while the collect thread collected
/// or swapped. It does not include <paramref name="RenderWaitForXrFrame"/>.
/// </param>
/// <param name="RenderWaitForXrFrame">
/// Time the render thread waited for a publication while the collect thread held it for
/// <c>xrWaitFrame</c> of the next XR frame (XR collect pacing). The render thread would wait for
/// the XR frame anyway, so this time is not collect cost.
/// </param>
/// <param name="CollectWaitForXrSubmit">
/// Time the collect thread waited, before it collected, for the render thread to end the XR frame
/// of the last publication (XR collect pacing).
/// </param>
/// <param name="PublicationAge">
/// Time from the start of a collect to the render thread taking its publication. One call for each
/// publication that the render thread took.
/// </param>
public readonly record struct FrameLifecyclePhaseTotals(
    FrameLifecyclePhaseTotal Update,
    FrameLifecyclePhaseTotal Collect,
    FrameLifecyclePhaseTotal Swap,
    FrameLifecyclePhaseTotal Render,
    FrameLifecyclePhaseTotal CollectWaitForRender,
    FrameLifecyclePhaseTotal RenderWaitForCollect,
    FrameLifecyclePhaseTotal RenderWaitForXrFrame,
    FrameLifecyclePhaseTotal CollectWaitForXrSubmit,
    FrameLifecyclePhaseTotal PublicationAge)
{
    /// <summary>Returns saturated non-negative deltas from <paramref name="baseline"/>.</summary>
    public FrameLifecyclePhaseTotals DeltaFrom(FrameLifecyclePhaseTotals baseline)
        => new(
            Update.DeltaFrom(baseline.Update),
            Collect.DeltaFrom(baseline.Collect),
            Swap.DeltaFrom(baseline.Swap),
            Render.DeltaFrom(baseline.Render),
            CollectWaitForRender.DeltaFrom(baseline.CollectWaitForRender),
            RenderWaitForCollect.DeltaFrom(baseline.RenderWaitForCollect),
            RenderWaitForXrFrame.DeltaFrom(baseline.RenderWaitForXrFrame),
            CollectWaitForXrSubmit.DeltaFrom(baseline.CollectWaitForXrSubmit),
            PublicationAge.DeltaFrom(baseline.PublicationAge));
}
