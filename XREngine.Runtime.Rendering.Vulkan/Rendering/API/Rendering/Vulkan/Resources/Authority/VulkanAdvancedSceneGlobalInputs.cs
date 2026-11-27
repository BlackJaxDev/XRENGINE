using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact CPU identity of the view/frame/pass data uploaded with a scene publication.
/// Storage belongs to a preallocated frame-slot entry and survives completed reuse.
/// </summary>
internal sealed class VulkanAdvancedSceneGlobalInputs
{
    private readonly BackendReadyCanonicalViewRecord[] _views = new BackendReadyCanonicalViewRecord[RenderFrameViewSet.MaxViewCount];
    private readonly AdvancedViewRecord[] _portalViews =
        new AdvancedViewRecord[AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame];
    private BackendReadyCanonicalPassRecord[] _passes = [];
    private AdvancedGlobalPassPublicationCoverage[] _coverage = [];
    private BackendReadyCanonicalFrameRecord _frame;
    private int _viewCount;
    private int _portalViewCount;
    private int _passCount;
    private int _coverageCount;
    private int _diagnosticCount;

    internal bool Matches(
        ReadOnlySpan<BackendReadyCanonicalViewRecord> views,
        in BackendReadyCanonicalFrameRecord frame,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        ReadOnlySpan<AdvancedGlobalPassPublicationCoverage> coverage,
        int diagnosticCount,
        ReadOnlySpan<AdvancedViewRecord> portalViews = default)
        => _frame == frame && _diagnosticCount == diagnosticCount &&
           views.SequenceEqual(_views.AsSpan(0, _viewCount)) &&
           PortalViewsEqual(_portalViews.AsSpan(0, _portalViewCount), portalViews) &&
           passes.SequenceEqual(_passes.AsSpan(0, _passCount)) &&
           coverage.SequenceEqual(_coverage.AsSpan(0, _coverageCount));

    internal void Capture(
        ReadOnlySpan<BackendReadyCanonicalViewRecord> views,
        in BackendReadyCanonicalFrameRecord frame,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        ReadOnlySpan<AdvancedGlobalPassPublicationCoverage> coverage,
        int diagnosticCount,
        ReadOnlySpan<AdvancedViewRecord> portalViews = default)
    {
        // Pass-layout growth occurs on admission of a new pipeline shape. Retain
        // the high-water storage so warmed frame-slot reuse does not allocate.
        if (_passes.Length < passes.Length)
            Array.Resize(ref _passes, passes.Length);
        if (_coverage.Length < coverage.Length)
            Array.Resize(ref _coverage, coverage.Length);
        views.CopyTo(_views);
        if (!portalViews.IsEmpty)
            portalViews.CopyTo(_portalViews);
        passes.CopyTo(_passes);
        coverage.CopyTo(_coverage);
        _viewCount = views.Length;
        _portalViewCount = portalViews.Length;
        _passCount = passes.Length;
        _coverageCount = coverage.Length;
        _frame = frame;
        _diagnosticCount = diagnosticCount;
    }

    private static bool PortalViewsEqual(
        ReadOnlySpan<AdvancedViewRecord> left,
        ReadOnlySpan<AdvancedViewRecord> right)
    {
        if (left.Length != right.Length)
            return false;
        if (left.IsEmpty)
            return true;
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(left)
            .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(right));
    }
}
