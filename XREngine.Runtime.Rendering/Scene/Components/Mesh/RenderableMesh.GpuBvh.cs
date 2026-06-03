using System.Threading;
using XREngine.Data.Geometry;
using XREngine.Rendering.Compute;

namespace XREngine.Components.Scene.Mesh;

public partial class RenderableMesh
{
    private readonly object _gpuMeshBvhLock = new();
    private GpuMeshBvh? _gpuMeshBvh;
    private int _gpuMeshBvhRefreshRequested;

    public GpuMeshBvh? GpuMeshBvh => _gpuMeshBvh;

    public bool HasCurrentGpuMeshBvh
        => HasCurrentGpuMeshBvhForMaxLeafPrimitives(XREngine.Rendering.Compute.GpuMeshBvh.DefaultMaxLeafPrimitives);

    public bool HasCurrentGpuMeshBvhForMaxLeafPrimitives(uint maxLeafPrimitives)
    {
        uint clampedMaxLeafPrimitives = maxLeafPrimitives == 0u ? 1u : maxLeafPrimitives;
        lock (_gpuMeshBvhLock)
            return _gpuMeshBvh?.IsBvhReady == true &&
                _gpuMeshBvh.MatchesSource(CurrentLODRenderer) &&
                _gpuMeshBvh.MaxLeafPrimitives == clampedMaxLeafPrimitives;
    }

    public bool PrepareGpuMeshBvh(
        bool realtimeSkinned,
        bool forceRebuild = false,
        uint maxLeafPrimitives = XREngine.Rendering.Compute.GpuMeshBvh.DefaultMaxLeafPrimitives)
    {
        lock (_gpuMeshBvhLock)
        {
            _gpuMeshBvh ??= new GpuMeshBvh();
            return _gpuMeshBvh.Prepare(this, realtimeSkinned, forceRebuild, maxLeafPrimitives);
        }
    }

    public void RequestGpuMeshBvhRefresh()
        => Interlocked.Exchange(ref _gpuMeshBvhRefreshRequested, 1);

    public bool HasGpuMeshBvhRefreshRequest
        => Volatile.Read(ref _gpuMeshBvhRefreshRequested) != 0;

    public void ClearGpuMeshBvhRefreshRequest()
        => Interlocked.Exchange(ref _gpuMeshBvhRefreshRequested, 0);

    public void ClearGpuMeshBvhRefreshRequestIfPrepared()
    {
        if (!HasGpuMeshBvhRefreshRequest)
            return;

        lock (_gpuMeshBvhLock)
        {
            if (!IsSkinned || _gpuMeshBvh?.LastUpdateUsedGpuSkinning == true)
                ClearGpuMeshBvhRefreshRequest();
        }
    }

    public bool TryConsumeGpuMeshBvhRefreshRequest()
        => Interlocked.Exchange(ref _gpuMeshBvhRefreshRequested, 0) != 0;

    public bool TryGetGpuMeshBvhRequestWorldBounds(out AABB worldBounds)
    {
        if (IsSkinned && RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader)
            return TryGetLastKnownGpuSkinnedWorldBounds(out worldBounds);

        return TryGetWorldBounds(out worldBounds);
    }

    internal void ProcessPendingGpuMeshBvhRefresh()
    {
        if (!RuntimeEngine.IsRenderThread || !HasGpuMeshBvhRefreshRequest)
            return;

        if (PrepareGpuMeshBvh(realtimeSkinned: true))
            ClearGpuMeshBvhRefreshRequestIfPrepared();
    }

    private void DisposeGpuMeshBvh()
    {
        lock (_gpuMeshBvhLock)
        {
            _gpuMeshBvh?.Dispose();
            _gpuMeshBvh = null;
            Interlocked.Exchange(ref _gpuMeshBvhRefreshRequested, 0);
        }
    }
}
