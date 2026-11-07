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

    /// <summary>View-pair argument that reads the complete published view set.</summary>
    public const int AllViews = -1;

    // Quad view renders its wide pair (views 0, 1) and inset pair (views 2, 3) as two
    // layered stereo frames. A reader that renders one pair asks for that pair's
    // two-view subset, built once per publication.
    private static readonly RenderFrameViewSet[] PairSubsets = new RenderFrameViewSet[2];
    private static bool _hasPairSubsets;
    private static readonly RenderFrameViewDescriptor[] PairScratch = new RenderFrameViewDescriptor[2];
    /// <summary>
    /// Returns the latest set, or one quad-view pair of it. Pair 0 is views 0 and 1;
    /// pair 1 is views 2 and 3. A pair request on a set that is not a four-view set
    /// returns the complete set. Caller holds <see cref="Sync"/>.
    /// </summary>
    private static RenderFrameViewSet ResolveNoLock(int viewPair)
        => (uint)viewPair <= 1u && _hasPairSubsets ? PairSubsets[viewPair] : _latest;

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
        => TryGet(frameId, AllViews, out viewSet);

    /// <summary>Reads the publication of <paramref name="frameId"/>, narrowed to <paramref name="viewPair"/>.</summary>
    public static bool TryGet(ulong frameId, int viewPair, out RenderFrameViewSet viewSet)
    {
        lock (Sync)
        {
            if (_hasLatest && _latestFrameId == frameId)
            {
                viewSet = ResolveNoLock(viewPair);
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
        => TryGetLatest(AllViews, out viewSet);

    /// <summary>Reads the latest publication, narrowed to <paramref name="viewPair"/>.</summary>
    public static bool TryGetLatest(int viewPair, out RenderFrameViewSet viewSet)
    {
        lock (Sync)
        {
            if (_hasLatest)
            {
                viewSet = ResolveNoLock(viewPair);
                return true;
            }

            viewSet = default;
            return false;
        }
    }

    /// <summary>Captures the exact current publication, including explicit absence.</summary>
    public static RenderFrameViewSetPublicationSnapshot CaptureLatest()
        => CaptureLatest(AllViews);

    /// <summary>Captures the current publication narrowed to <paramref name="viewPair"/>.</summary>
    public static RenderFrameViewSetPublicationSnapshot CaptureLatest(int viewPair)
    {
        lock (Sync)
        {
            return new RenderFrameViewSetPublicationSnapshot(
                _revision,
                _hasLatest,
                _hasLatest ? ResolveNoLock(viewPair) : _latest);
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
