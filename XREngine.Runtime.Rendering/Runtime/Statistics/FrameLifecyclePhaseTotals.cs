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
/// <param name="RenderWaitForCollect">Time the render thread waited for a visibility publication.</param>
public readonly record struct FrameLifecyclePhaseTotals(
    FrameLifecyclePhaseTotal Update,
    FrameLifecyclePhaseTotal Collect,
    FrameLifecyclePhaseTotal Swap,
    FrameLifecyclePhaseTotal Render,
    FrameLifecyclePhaseTotal CollectWaitForRender,
    FrameLifecyclePhaseTotal RenderWaitForCollect)
{
    /// <summary>Returns saturated non-negative deltas from <paramref name="baseline"/>.</summary>
    public FrameLifecyclePhaseTotals DeltaFrom(FrameLifecyclePhaseTotals baseline)
        => new(
            Update.DeltaFrom(baseline.Update),
            Collect.DeltaFrom(baseline.Collect),
            Swap.DeltaFrom(baseline.Swap),
            Render.DeltaFrom(baseline.Render),
            CollectWaitForRender.DeltaFrom(baseline.CollectWaitForRender),
            RenderWaitForCollect.DeltaFrom(baseline.RenderWaitForCollect));
}
