namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Allocation-free diagnostics produced alongside a CPU-query camera-motion
/// decision. Camera identities are observational only: recreated cameras remain
/// valid when their pose and projection are continuous.
/// </summary>
internal readonly record struct CpuOcclusionMotionClassification(
    ECpuOcclusionMotionTier Tier,
    ECpuOcclusionMotionCause Cause,
    float DistanceMeters,
    float RotationDegrees,
    float ProjectionDelta,
    bool PreviousSnapshotValid,
    bool CurrentSnapshotValid,
    int PreviousCameraIdentity,
    int CurrentCameraIdentity);
