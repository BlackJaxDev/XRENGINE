using XREngine.Data.Geometry;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;

namespace XREngine.Rendering;

public partial class XRMeshRenderer : ICommittedWorldBoundsProvider
{
    /// <summary>Reports a committed spatial output or its invalidation.</summary>
    public event Action<XRMeshRenderer, uint, bool>? CommittedWorldBoundsChanged;

    /// <summary>Reads the query bound of the exact committed GPU output.</summary>
    public bool TryGetCommittedWorldBounds(out AABB bounds, out ulong outputGeneration)
        => GPUPhysicsChainDispatcher.Instance.TryGetCommittedSpatialBounds(this, out bounds, out outputGeneration);

    /// <summary>Gets the admission state of the committed CPU spatial bound.</summary>
    internal EPhysicsChainSpatialBoundsStatus CommittedSpatialBoundsStatus
        => GPUPhysicsChainDispatcher.Instance.GetCommittedSpatialBoundsStatus(this);

    internal void NotifyCommittedWorldBoundsChanged(uint producerEpoch, bool published)
        => CommittedWorldBoundsChanged?.Invoke(this, producerEpoch, published);
}
