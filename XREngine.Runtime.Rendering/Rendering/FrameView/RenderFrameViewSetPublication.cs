namespace XREngine.Rendering;

/// <summary>
/// Publishes the runtime view set for one exact render frame to render-state capture on the render thread.
/// </summary>
public static class RenderFrameViewSetPublication
{
    private static readonly object Sync = new();
    private static RenderFrameViewSet _latest;
    private static ulong _latestFrameId;
    private static ulong _revision;
    private static bool _hasLatest;

    // Quad view renders its wide pair (views 0, 1) and inset pair (views 2, 3) as two
    // layered stereo frames. Each pair must see only its own two views, so a pair
    // scope on the current thread narrows every read to that pair's subset.
    private static readonly RenderFrameViewSet[] PairSubsets = new RenderFrameViewSet[2];
    private static bool _hasPairSubsets;
    private static readonly RenderFrameViewDescriptor[] PairScratch = new RenderFrameViewDescriptor[2];
    [ThreadStatic]
    private static int t_pairScope;

    /// <summary>
    /// Narrows the view set that this thread reads to one quad-view pair until the
    /// returned scope is disposed. Pair 0 is views 0 and 1; pair 1 is views 2 and 3.
    /// Has no effect when the latest publication is not a four-view set.
    /// </summary>
    public static ViewPairScope EnterViewPairScope(int pair)
    {
        if ((uint)pair > 1u)
            throw new ArgumentOutOfRangeException(nameof(pair));
        int previous = t_pairScope;
        t_pairScope = pair + 1;
        return new ViewPairScope(previous);
    }

    /// <summary>Restores the previous view-pair scope of the current thread.</summary>
    public readonly struct ViewPairScope(int previous) : IDisposable
    {
        public void Dispose() => t_pairScope = previous;
    }

    /// <summary>Returns the latest set, or the scoped pair's subset. Caller holds <see cref="Sync"/>.</summary>
    private static RenderFrameViewSet ResolveScopedNoLock()
    {
        int scope = t_pairScope;
        return scope != 0 && _hasPairSubsets ? PairSubsets[scope - 1] : _latest;
    }

    private static void RebuildPairSubsetsNoLock()
    {
        _hasPairSubsets = _latest.ViewCount == 4;
        if (!_hasPairSubsets)
            return;

        // Reused pair buffer; only the publishing caller writes it under Sync.
        Span<RenderFrameViewDescriptor> pair = PairScratch;
        for (int pairIndex = 0; pairIndex < 2; pairIndex++)
        {
            for (int slot = 0; slot < 2; slot++)
            {
                // Pair views get dense IDs 0 and 1 (their array layers). History keys
                // and OpenXR view indexes stay those of the located views.
                RenderFrameViewDescriptor view = _latest.GetView(pairIndex * 2 + slot);
                pair[slot] = view with
                {
                    ViewId = (uint)slot,
                    ParentViewId = RenderFrameViewDescriptor.InvalidViewId,
                    ParentContainsView = false,
                };
            }
            PairSubsets[pairIndex] = RenderFrameViewSet.Create(
                _latest.RenderMode,
                _latest.VisibilityPolicy,
                _latest.VisibilityGroupCount,
                pair,
                pairIndex == 0 ? "OpenXR quad wide pair" : "OpenXR quad inset pair");
        }
    }

    /// <summary>
    /// Publishes <paramref name="viewSet"/> for <paramref name="frameId"/>. The
    /// revision changes only when the view set changes. OpenXR publishes its
    /// located views again on every collect until the next locate, and a revision
    /// change for an equal set would invalidate each consumer that captured it.
    /// </summary>
    public static void Publish(ulong frameId, in RenderFrameViewSet viewSet)
    {
        lock (Sync)
        {
            if (_hasLatest && _latest.Equals(viewSet))
            {
                _latestFrameId = frameId;
                return;
            }

            _latest = viewSet;
            _latestFrameId = frameId;
            _hasLatest = true;
            _revision++;
            RebuildPairSubsetsNoLock();
        }
    }

    public static bool TryGet(ulong frameId, out RenderFrameViewSet viewSet)
    {
        lock (Sync)
        {
            if (_hasLatest && _latestFrameId == frameId)
            {
                viewSet = ResolveScopedNoLock();
                return true;
            }

            viewSet = default;
            return false;
        }
    }

    /// <summary>
    /// Resolves the most recently located immutable view set. OpenXR owns one pending
    /// runtime frame at a time, while the desktop render-frame counter can advance more
    /// than once during eye-resource warmup or a rejected desktop frame. Eye render and
    /// planning therefore consume the latest publication until the next locate replaces it.
    /// </summary>
    public static bool TryGetLatest(out RenderFrameViewSet viewSet)
    {
        lock (Sync)
        {
            if (_hasLatest)
            {
                viewSet = ResolveScopedNoLock();
                return true;
            }

            viewSet = default;
            return false;
        }
    }

    /// <summary>Captures the exact current publication, including explicit absence.</summary>
    public static RenderFrameViewSetPublicationSnapshot CaptureLatest()
    {
        lock (Sync)
        {
            return new RenderFrameViewSetPublicationSnapshot(
                _revision,
                _hasLatest,
                _hasLatest ? ResolveScopedNoLock() : _latest);
        }
    }

    /// <summary>Returns whether the publication has not changed since capture.</summary>
    public static bool IsCurrent(ulong revision)
    {
        lock (Sync)
            return _revision == revision;
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _latest = default;
            _latestFrameId = 0UL;
            _hasLatest = false;
            _hasPairSubsets = false;
            _revision++;
        }
    }
}
