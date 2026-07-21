using System.Numerics;

namespace XREngine;

public readonly record struct StableViewHiZRequest(
    ulong StableViewKey,
    ulong StableOuterEyeKey,
    uint LogicalViewId,
    uint OuterEyeLogicalViewId,
    Matrix4x4 ExactViewProjection,
    Matrix4x4 OuterEyeViewProjection,
    ulong ResourceGeneration,
    ulong SceneRevision,
    EHiZHistoryMode RequestedMode,
    bool IsInset,
    bool OuterContainsInset,
    bool ProjectionCompatible,
    bool DepthConventionCompatible,
    bool CameraCut,
    bool TrackingJump,
    bool ProjectionDiscontinuity,
    bool UnsafeSceneRevision);
