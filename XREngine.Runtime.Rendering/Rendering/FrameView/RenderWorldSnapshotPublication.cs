namespace XREngine.Rendering;

/// <summary>
/// Publishes exactly one immutable render-world token for each engine render frame.
/// Subsequent output/family states acquire the first token and therefore cannot
/// observe a different scene buffer generation in that frame.
/// </summary>
public static class RenderWorldSnapshotPublication
{
    [ThreadStatic]
    private static IRuntimeRenderCommandSceneContext? s_isolatedScene;
    [ThreadStatic]
    private static RenderWorldSnapshot? s_isolatedPublished;

    /// <summary>Gives a privately owned capture scene its own token while preserving the main frame publication.</summary>
    public static IsolatedSceneScope EnterIsolatedScene(IRuntimeRenderCommandSceneContext scene) => new(scene);

    public readonly struct IsolatedSceneScope : IDisposable
    {
        private readonly IRuntimeRenderCommandSceneContext? _previousScene;
        private readonly RenderWorldSnapshot? _previousSnapshot;
        internal IsolatedSceneScope(IRuntimeRenderCommandSceneContext scene)
        {
            ArgumentNullException.ThrowIfNull(scene);
            _previousScene = s_isolatedScene;
            _previousSnapshot = s_isolatedPublished;
            s_isolatedScene = scene;
            s_isolatedPublished = null;
        }
        public void Dispose()
        {
            s_isolatedScene = _previousScene;
            s_isolatedPublished = _previousSnapshot;
        }
    }
    private static readonly object Sync = new();
    private static RenderWorldSnapshot _published;
    private static bool _hasPublished;

    public static RenderWorldSnapshot Acquire(
        ulong frameId,
        IRuntimeRenderCommandSceneContext scene,
        GPUScene gpuScene,
        in AdvancedGlobalResourceCapture globalResources)
    {
        if (ReferenceEquals(s_isolatedScene, scene))
        {
            if (s_isolatedPublished is { } isolated && isolated.FrameId == frameId && ReferenceEquals(isolated.GpuScene, gpuScene))
                return isolated;
            RenderWorldSnapshot captured = new(frameId, scene, gpuScene, globalResources);
            s_isolatedPublished = captured;
            return captured;
        }
        lock (Sync)
        {
            if (_hasPublished && _published.FrameId == frameId)
                return _published;

            _published = new RenderWorldSnapshot(frameId, scene, gpuScene, globalResources);
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
