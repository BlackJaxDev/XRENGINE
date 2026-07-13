using System;
using System.Threading;
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
/// Immutable ownership assigned by the output that publishes a render-command
/// collection. A physical pipeline owns its query objects, while <see cref="PovId"/>
/// groups every view that must contribute to one conservative visibility result.
/// </summary>
public readonly struct OcclusionViewOwnership : IEquatable<OcclusionViewOwnership>
{
    private static int s_nextPovId;

    public OcclusionViewOwnership(
        int pipelineInstanceId,
        int povId,
        EOcclusionViewScope scope,
        uint coverageMask,
        uint requiredCoverageMask,
        int declaredViewCount,
        int resourceGeneration = 0,
        bool hasScopeOverride = true,
        ulong outputId = 0UL)
    {
        PipelineInstanceId = pipelineInstanceId;
        PovId = povId;
        Scope = scope;
        CoverageMask = coverageMask;
        RequiredCoverageMask = requiredCoverageMask;
        DeclaredViewCount = Math.Clamp(declaredViewCount, 1, 32);
        ResourceGeneration = resourceGeneration;
        HasScopeOverride = hasScopeOverride;
        OutputId = outputId != 0UL ? outputId : unchecked((ulong)(uint)pipelineInstanceId);
    }

    public int PipelineInstanceId { get; }
    public int PovId { get; }
    public EOcclusionViewScope Scope { get; }
    public uint CoverageMask { get; }
    public uint RequiredCoverageMask { get; }
    public int DeclaredViewCount { get; }
    public int ResourceGeneration { get; }
    public bool HasScopeOverride { get; }
    public ulong OutputId { get; }

    public bool IsValid
        => PipelineInstanceId > 0 &&
           PovId != 0 &&
           CoverageMask != 0u &&
           RequiredCoverageMask != 0u &&
           (CoverageMask & ~RequiredCoverageMask) == 0u;

    public bool SharesConservativeResult
        => IsValid && (RequiredCoverageMask & (RequiredCoverageMask - 1u)) != 0u;

    /// <summary>
    /// Allocates a process-stable family identity. Negative IDs cannot collide
    /// with the positive, monotonically allocated pipeline-instance IDs.
    /// </summary>
    public static int AllocatePovId()
        => -Interlocked.Increment(ref s_nextPovId);

    public static OcclusionViewOwnership Independent(
        int pipelineInstanceId,
        int resourceGeneration = 0,
        ulong outputId = 0UL)
        => new(
            pipelineInstanceId,
            pipelineInstanceId,
            EOcclusionViewScope.MonoDesktop,
            coverageMask: 0x1u,
            requiredCoverageMask: 0x1u,
            declaredViewCount: 1,
            resourceGeneration,
            hasScopeOverride: false,
            outputId: outputId);

    public OcclusionViewOwnership WithResourceGeneration(int resourceGeneration)
        => new(
            PipelineInstanceId,
            PovId,
            Scope,
            CoverageMask,
            RequiredCoverageMask,
            DeclaredViewCount,
            resourceGeneration,
            HasScopeOverride,
            OutputId);

    public bool Equals(OcclusionViewOwnership other)
        => PipelineInstanceId == other.PipelineInstanceId &&
           PovId == other.PovId &&
           Scope == other.Scope &&
           CoverageMask == other.CoverageMask &&
           RequiredCoverageMask == other.RequiredCoverageMask &&
           DeclaredViewCount == other.DeclaredViewCount &&
           ResourceGeneration == other.ResourceGeneration &&
           HasScopeOverride == other.HasScopeOverride &&
           OutputId == other.OutputId;

    public override bool Equals(object? obj)
        => obj is OcclusionViewOwnership other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            HashCode.Combine(
                PipelineInstanceId,
                PovId,
                Scope,
                CoverageMask,
                RequiredCoverageMask,
                DeclaredViewCount,
                ResourceGeneration,
                HasScopeOverride),
            OutputId);
}

/// <summary>
/// Stable key for per-pass CPU occlusion state. It intentionally avoids camera
/// object identity so recreated eye cameras do not look first-seen every frame.
/// State is isolated per render pipeline instance so the desktop view, each VR
/// eye, and capture/preview cameras track occlusion completely independently.
/// </summary>
public readonly struct OcclusionViewKey : IEquatable<OcclusionViewKey>
{
    public OcclusionViewKey(
        int renderPass,
        EOcclusionViewScope scope,
        int viewId = 0,
        int pipelineInstanceId = 0,
        int povId = 0,
        uint coverageMask = 0x1u,
        uint requiredCoverageMask = 0x1u,
        int declaredViewCount = 1,
        int resourceGeneration = 0,
        ulong outputId = 0UL)
    {
        RenderPass = renderPass;
        Scope = scope;
        ViewId = viewId;
        PipelineInstanceId = pipelineInstanceId;
        PovId = povId != 0 ? povId : pipelineInstanceId;
        CoverageMask = coverageMask;
        RequiredCoverageMask = requiredCoverageMask;
        DeclaredViewCount = declaredViewCount;
        ResourceGeneration = resourceGeneration;
        OutputId = outputId != 0UL ? outputId : unchecked((ulong)(uint)pipelineInstanceId);
    }

    public int RenderPass { get; }
    public EOcclusionViewScope Scope { get; }
    public int ViewId { get; }
    public int PipelineInstanceId { get; }
    public int PovId { get; }
    public uint CoverageMask { get; }
    public uint RequiredCoverageMask { get; }
    public int DeclaredViewCount { get; }
    public int ResourceGeneration { get; }
    public ulong OutputId { get; }

    public bool Equals(OcclusionViewKey other)
        => RenderPass == other.RenderPass &&
           Scope == other.Scope &&
           ViewId == other.ViewId &&
           PipelineInstanceId == other.PipelineInstanceId &&
           PovId == other.PovId &&
           CoverageMask == other.CoverageMask &&
           RequiredCoverageMask == other.RequiredCoverageMask &&
           DeclaredViewCount == other.DeclaredViewCount &&
           ResourceGeneration == other.ResourceGeneration &&
           OutputId == other.OutputId;

    public override bool Equals(object? obj)
        => obj is OcclusionViewKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            HashCode.Combine(
                RenderPass,
                Scope,
                ViewId,
                PipelineInstanceId,
                PovId,
                CoverageMask,
                RequiredCoverageMask,
                HashCode.Combine(DeclaredViewCount, ResourceGeneration)),
            OutputId);

    public override string ToString()
        => $"{Scope}:pass{RenderPass}:view{ViewId}:pipe{PipelineInstanceId}:output0x{OutputId:X}:pov{PovId}:coverage0x{CoverageMask:X}/0x{RequiredCoverageMask:X}:views{DeclaredViewCount}:gen{ResourceGeneration}";

    public bool IsSharedStereoScope
        => Scope is EOcclusionViewScope.VrStereoPair
            or EOcclusionViewScope.VrSinglePassStereo
            or EOcclusionViewScope.VrFoveatedView;
}

/// <summary>Per-output CPU-query statistics for the last completed frame.</summary>
public readonly record struct CpuOcclusionViewTelemetrySnapshot(
    OcclusionViewKey ViewKey,
    int CandidateCount,
    int Submissions,
    int Resolutions,
    int Skips,
    int BudgetSkipped,
    int ForcedVisible,
    int RecoveryStarts,
    int RecoveryCompletions,
    int CurrentRecoveryAgeFrames,
    int MaxRecoveryAgeFrames,
    int CurrentResultAgeFrames,
    int MaxResultAgeFrames,
    int RecoveryLatencyFrames);

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
    MissingOwnership,
    StaleResult,
    ResourceGenerationChanged,
    CommandSetChanged,
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
