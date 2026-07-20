using XREngine.Data.Geometry;

namespace XREngine.Rendering.Occlusion;

/// <summary>Resolved GPU-dispatch query result plus the pose and proxy that produced it.</summary>
internal readonly struct CpuOcclusionTemporalResult
{
    internal CpuOcclusionTemporalResult(
        bool anySamplesPassed,
        in CpuOcclusionCameraSnapshot queryCamera,
        in AABB queriedBounds,
        ulong resolvedFrame)
    {
        AnySamplesPassed = anySamplesPassed;
        QueryCamera = queryCamera;
        QueriedBounds = queriedBounds;
        ResolvedFrame = resolvedFrame;
    }

    internal bool AnySamplesPassed { get; }
    internal CpuOcclusionCameraSnapshot QueryCamera { get; }
    internal AABB QueriedBounds { get; }
    internal ulong ResolvedFrame { get; }
}
