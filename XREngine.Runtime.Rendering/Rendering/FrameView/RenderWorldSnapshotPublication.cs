namespace XREngine.Rendering;

/// <summary>
/// Publishes exactly one immutable render-world token for each engine render frame.
/// Subsequent output/family states acquire the first token and therefore cannot
/// observe a different scene buffer generation in that frame.
/// </summary>
public static class RenderWorldSnapshotPublication
{
    private static readonly object Sync = new();
    private static RenderWorldSnapshot _published;
    private static bool _hasPublished;

    public static RenderWorldSnapshot Acquire(
        ulong frameId,
        IRuntimeRenderCommandSceneContext scene,
        GPUScene gpuScene)
    {
        lock (Sync)
        {
            if (_hasPublished && _published.FrameId == frameId)
                return _published;

            _published = new RenderWorldSnapshot(frameId, scene, gpuScene);
            _hasPublished = true;
            return _published;
        }
    }

    public static bool TryGet(ulong frameId, out RenderWorldSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = _published;
            return _hasPublished && _published.FrameId == frameId;
        }
    }
}