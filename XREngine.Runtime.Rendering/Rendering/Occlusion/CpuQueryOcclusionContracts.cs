using System;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Stable scope for CPU hardware-query occlusion state. Shared stereo scopes are
/// conservative by contract: a command may be culled only when the result is
/// known safe for every eye represented by the scope.
/// </summary>
public enum EOcclusionViewScope
{
    MonoDesktop,
    EditorDesktopWhileVr,
    VrLeftEye,
    VrRightEye,
    VrStereoPair,
    VrSinglePassStereo,
    VrFoveatedView,
    MirrorOnly,
}

/// <summary>
/// Stable key for per-pass CPU occlusion state. It intentionally avoids camera
/// object identity so recreated eye cameras do not look first-seen every frame.
/// </summary>
public readonly struct OcclusionViewKey : IEquatable<OcclusionViewKey>
{
    public OcclusionViewKey(int renderPass, EOcclusionViewScope scope, int viewId = 0)
    {
        RenderPass = renderPass;
        Scope = scope;
        ViewId = viewId;
    }

    public int RenderPass { get; }
    public EOcclusionViewScope Scope { get; }
    public int ViewId { get; }

    public bool Equals(OcclusionViewKey other)
        => RenderPass == other.RenderPass && Scope == other.Scope && ViewId == other.ViewId;

    public override bool Equals(object? obj)
        => obj is OcclusionViewKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(RenderPass, Scope, ViewId);

    public override string ToString()
        => $"{Scope}:{RenderPass}:{ViewId}";

    public bool IsSharedStereoScope
        => Scope is EOcclusionViewScope.VrStereoPair
            or EOcclusionViewScope.VrSinglePassStereo
            or EOcclusionViewScope.VrFoveatedView;
}

public enum ECpuOcclusionMotionTier
{
    Stable,
    SmallMotion,
    MediumMotion,
    LargeMotion,
    VrHeadPoseMotion,
    CameraCut,
}

public enum ECpuOcclusionQueryStateKind
{
    Unknown,
    PredictedVisible,
    PredictedOccluded,
    PendingVisibleProbe,
    PendingOccludedProbe,
    ForcedVisible,
}

public enum ECpuOcclusionQueryReason
{
    None,
    VisibleDemotion,
    OccludedRecovery,
    InitialSeed,
    CameraMotionRevalidation,
    StaleStateRefresh,
    DiagnosticForcedQuery,
}

public enum ECpuOcclusionForceVisibleReason
{
    None,
    CameraCut,
    ProjectionDiscontinuity,
    MissingCameraState,
    NearPlaneUnsafe,
    UnsupportedBackend,
    UnsupportedStereoMode,
    PendingTooOld,
    NoBounds,
    Diagnostic,
}

public enum ECpuQueryStereoMode
{
    ConservativeSharedVisible,
    PerEyeSequential,
    StereoPairShared,
}

public readonly struct CpuOcclusionProbeRequest
{
    public CpuOcclusionProbeRequest(
        bool requested,
        ECpuOcclusionQueryReason reason,
        bool recoveryProbe,
        float priorityBias = 0.0f)
    {
        Requested = requested;
        Reason = reason;
        RecoveryProbe = recoveryProbe;
        PriorityBias = priorityBias;
    }

    public bool Requested { get; }
    public ECpuOcclusionQueryReason Reason { get; }
    public bool RecoveryProbe { get; }
    public float PriorityBias { get; }

    public static CpuOcclusionProbeRequest None { get; } = new(false, ECpuOcclusionQueryReason.None, false);
}

public readonly struct CpuOcclusionProbeCandidate
{
    public CpuOcclusionProbeCandidate(
        uint queryKey,
        AABB worldBounds,
        CpuOcclusionProbeRequest request,
        float screenPriority,
        float distanceMeters)
    {
        QueryKey = queryKey;
        WorldBounds = worldBounds;
        Request = request;
        ScreenPriority = screenPriority;
        DistanceMeters = distanceMeters;
    }

    public uint QueryKey { get; }
    public AABB WorldBounds { get; }
    public CpuOcclusionProbeRequest Request { get; }
    public float ScreenPriority { get; }
    public float DistanceMeters { get; }
}

public readonly struct CpuOcclusionScheduledProbe
{
    public CpuOcclusionScheduledProbe(uint queryKey, AABB worldBounds, bool isHierarchyGroup, uint hierarchyGroupKey)
    {
        QueryKey = queryKey;
        WorldBounds = worldBounds;
        IsHierarchyGroup = isHierarchyGroup;
        HierarchyGroupKey = hierarchyGroupKey;
    }

    public uint QueryKey { get; }
    public AABB WorldBounds { get; }
    public bool IsHierarchyGroup { get; }
    public uint HierarchyGroupKey { get; }
}
