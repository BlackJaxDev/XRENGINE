using XREngine.Data.Geometry;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;

namespace XREngine.Rendering;

public partial class XRMeshRenderer : ICommittedWorldBoundsProvider
{
    /// <summary>
    /// Reports a committed GPU output or its invalidation. The arguments are the renderer, the
    /// producer epoch, whether the output is published, and whether the committed world bound
    /// changed. A published output with an unchanged bound still changes the GPU pose.
    /// </summary>
    public event Action<XRMeshRenderer, uint, bool, bool>? CommittedOutputChanged;

    /// <summary>Reads the query bound of the exact committed GPU output.</summary>
    public bool TryGetCommittedWorldBounds(out AABB bounds, out ulong outputGeneration)
        => GPUPhysicsChainDispatcher.Instance.TryGetCommittedSpatialBounds(this, out bounds, out outputGeneration);

    /// <summary>Gets the admission state of the committed CPU spatial bound.</summary>
    internal EPhysicsChainSpatialBoundsStatus CommittedSpatialBoundsStatus
        => GPUPhysicsChainDispatcher.Instance.GetCommittedSpatialBoundsStatus(this);

    internal void NotifyCommittedOutputChanged(uint producerEpoch, bool published, bool boundsChanged)
        => CommittedOutputChanged?.Invoke(this, producerEpoch, published, boundsChanged);
}
