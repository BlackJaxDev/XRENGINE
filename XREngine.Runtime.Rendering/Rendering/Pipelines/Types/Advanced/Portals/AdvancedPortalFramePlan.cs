namespace XREngine.Rendering;

/// <summary>
/// Preallocated, zero-allocation per-frame schedule of portal and mirror sub-views produced
/// by <see cref="AdvancedPortalViewScheduler"/> and consumed by Vulkan scene upload,
/// visibility raster, and late unwind stages.
/// </summary>
public sealed class AdvancedPortalFramePlan
{
    private readonly AdvancedScheduledPortalView[] _nodes =
        new AdvancedScheduledPortalView[AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame];
    private readonly AdvancedViewRecord[] _portalViews =
        new AdvancedViewRecord[AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame];

    private int _count;

    /// <summary>Number of primary views preceding the scheduled portal sub-views in <c>XR_ADV_Views</c>.</summary>
    public int PrimaryViewCount { get; internal set; }

    /// <summary>Number of scheduled portal sub-views in this frame plan.</summary>
    public int Count => _count;

    /// <summary>Highest recursion depth scheduled in this frame (<c>0</c> when no portals are scheduled).</summary>
    public int MaxScheduledDepth { get; internal set; }

    /// <summary>Count of portal candidates rejected because the frame or per-eye view budget was full.</summary>
    public int RejectedBudgetCount { get; internal set; }

    /// <summary>Count of portal candidates rejected by back-face, frustum, or screen-area culling.</summary>
    public int RejectedCulledCount { get; internal set; }

    /// <summary>Count of recursive portal candidates rejected by self-recursion or recursion-disabled policy.</summary>
    public int RejectedCycleCount { get; internal set; }

    /// <summary>Scheduled portal nodes in outside-in breadth-first order (depth 1 to <see cref="MaxScheduledDepth"/>).</summary>
    public ReadOnlySpan<AdvancedScheduledPortalView> ScheduledNodes => _nodes.AsSpan(0, _count);

    /// <summary>Corresponding GPU view records appended after primary views in <c>XR_ADV_Views</c>.</summary>
    public ReadOnlySpan<AdvancedViewRecord> PortalViewRecords => _portalViews.AsSpan(0, _count);

    /// <summary>Resets all counts for the next frame without releasing preallocated storage.</summary>
    public void Reset()
    {
        _count = 0;
        PrimaryViewCount = 0;
        MaxScheduledDepth = 0;
        RejectedBudgetCount = 0;
        RejectedCulledCount = 0;
        RejectedCycleCount = 0;
    }

    /// <summary>
    /// Copies a scheduled frame plan into this preallocated instance without heap allocations.
    /// </summary>
    public void CopyFrom(AdvancedPortalFramePlan? source)
    {
        if (source is null || source._count == 0)
        {
            Reset();
            return;
        }

        int count = Math.Min(source._count, _nodes.Length);
        source._nodes.AsSpan(0, count).CopyTo(_nodes);
        source._portalViews.AsSpan(0, count).CopyTo(_portalViews);
        _count = count;
        PrimaryViewCount = source.PrimaryViewCount;
        MaxScheduledDepth = source.MaxScheduledDepth;
        RejectedBudgetCount = source.RejectedBudgetCount;
        RejectedCulledCount = source.RejectedCulledCount;
        RejectedCycleCount = source.RejectedCycleCount;
    }

    internal bool TryAdd(in AdvancedScheduledPortalView node, in AdvancedViewRecord viewRecord)
    {
        if (_count >= _nodes.Length)
            return false;

        _nodes[_count] = node;
        _portalViews[_count] = viewRecord;
        ++_count;
        if (node.RecursionDepth > MaxScheduledDepth)
            MaxScheduledDepth = (int)node.RecursionDepth;
        return true;
    }
}
