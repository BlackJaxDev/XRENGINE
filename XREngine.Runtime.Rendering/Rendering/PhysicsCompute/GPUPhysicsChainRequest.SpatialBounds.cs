using XREngine.Data.Geometry;

namespace XREngine.Rendering.Compute;

public partial class GPUPhysicsChainRequest
{
    internal AABB ResidentParticleBounds;
    internal float ResidentBasisStretch;
    internal bool ResidentSpatialValid;
    internal bool ResidentSpatialInitialized;
    internal bool ResidentTightSpatialValid;
    internal ulong ResidentSpatialRevision;
    internal bool ResidentSolveAttempted;
    internal bool ResidentSolveCompleted;
    internal AABB ResidentTightParticleBounds;
    internal float ResidentTightBasisStretch;
}
