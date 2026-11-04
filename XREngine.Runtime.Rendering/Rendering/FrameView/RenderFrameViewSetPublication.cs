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
        }
    }

    public static bool TryGet(ulong frameId, out RenderFrameViewSet viewSet)
    {
        lock (Sync)
        {
            if (_hasLatest && _latestFrameId == frameId)
            {
                viewSet = _latest;
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
                viewSet = _latest;
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
                _latest);
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
            _revision++;
        }
    }
}
