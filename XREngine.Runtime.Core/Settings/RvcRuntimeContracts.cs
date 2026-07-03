using System.Numerics;

namespace XREngine;

public enum ERvcCounterReadbackMode
{
    Disabled,
    DelayedGpuReadback,
}

public enum ERvcCounterReadbackDecision
{
    Disabled,
    Pending,
    Ready,
    SynchronousForbidden,
}

public enum ERvcResourceBarrierBackend
{
    OpenGlMemoryBarrier,
    VulkanSynchronization2,
    VulkanDynamicRenderingAttachment,
    VulkanTimelineSemaphoreHandoff,
}

[Flags]
public enum ERvcResourceAliasRule
{
    None = 0,
    SameViewOnly = 1 << 0,
    NoReadAfterWriteOverlap = 1 << 1,
    NoHistoryAlias = 1 << 2,
    DebugResourcesNeverAlias = 1 << 3,
    ExternalSwapchainNeverAlias = 1 << 4,
}

[Flags]
public enum ERvcVisibilityExecutionLane
{
    None = 0,
    HardwareRaster = 1 << 0,
    MeshletCompute = 1 << 1,
    MeshShader = 1 << 2,
    TinyTriangleSoftwareRaster = 1 << 3,
    ForwardPlusFallback = 1 << 4,
}

public enum ERvcVisibilityPayloadFormat
{
    Rg32UintIdentity64,
    R32UintPacked,
    BackendNative64,
}

public enum ERvcVisibilityCandidateClass
{
    StableVisible,
    PreviouslyVisibleHzbRejectable,
    NewlyVisible,
    Uncertain,
    HzbEdge,
    CrossViewDisagreement,
    DynamicObject,
}

[Flags]
public enum ERvcEdgeRejectionFlags
{
    None = 0,
    Depth = 1 << 0,
    Normal = 1 << 1,
    Material = 1 << 2,
    Primitive = 1 << 3,
    Disocclusion = 1 << 4,
}

public enum ERvcDerivativeStrategy
{
    AnalyticFromVisibilityGradients,
    FineDerivativeFallback,
    MaterialProvided,
}

[Flags]
public enum ERvcReuseDomain
{
    None = 0,
    IntraView = 1 << 0,
    InsetWide = 1 << 1,
    Stereo = 1 << 2,
    Temporal = 1 << 3,
}

[Flags]
public enum ERvcMaterialViewDependency
{
    None = 0,
    SharpSpecular = 1 << 0,
    ReflectionProbeParallax = 1 << 1,
    Refraction = 1 << 2,
    ParallaxOcclusion = 1 << 3,
    VirtualDisplacement = 1 << 4,
    ScreenSpaceEffect = 1 << 5,
}

[Flags]
public enum ERvcTemporalInvalidationReason
{
    None = 0,
    MaterialChanged = 1 << 0,
    MaterialResourceGenerationChanged = 1 << 1,
    AnimationOrDeformationChanged = 1 << 2,
    LodChanged = 1 << 3,
    ShadowCasterSetChanged = 1 << 4,
    ViewSetChanged = 1 << 5,
    GazeRegionChanged = 1 << 6,
    TopologyChanged = 1 << 7,
}

public enum ERvcFovealAntiAliasingPath
{
    VisibilityEdgeAA,
    FoveatedTaaFallback,
    Disabled,
}

[Flags]
public enum ERvcVisibilitySourcePath
{
    None = 0,
    StaticMeshDirect = 1 << 0,
    SkinnedComputeOutput = 1 << 1,
    ZeroReadbackMaterialTable = 1 << 2,
    MeshletTaskExpansion = 1 << 3,
    ForwardPlusOracle = 1 << 4,
}

public enum ERvcOpenXrVisibilityMaskStatus
{
    NotRequested,
    ExtensionMissing,
    NativeFunctionMissing,
    AwaitingRuntimeMesh,
    RuntimeMeshUnavailable,
    ReadyForStencilPrepass,
    InvalidatedByRuntime,
}

[Flags]
public enum ERvcGpuPassStage
{
    None = 0,
    VisibilityTargets = 1 << 0,
    OpenXrVisibilityMaskStencil = 1 << 1,
    AttributeReconstruction = 1 << 2,
    HzbRejection = 1 << 3,
    PixelToShadeletMap = 1 << 4,
    MaterialShadeletShading = 1 << 5,
    FoveatedShadingRate = 1 << 6,
    HeadSpaceLightClusters = 1 << 7,
    SharedLighting = 1 << 8,
    ReuseValidation = 1 << 9,
    TemporalCache = 1 << 10,
    FoveatedResolve = 1 << 11,
    TransparencyForwardPlus = 1 << 12,
    DiagnosticOverlay = 1 << 13,
}

[Flags]
public enum ERvcVulkanProductionFeature
{
    None = 0,
    Multiview = 1 << 0,
    DynamicRendering = 1 << 1,
    Synchronization2 = 1 << 2,
    DescriptorIndexing = 1 << 3,
    FragmentShadingRate = 1 << 4,
    FragmentDensityMap = 1 << 5,
    MeshShader = 1 << 6,
    TimelineSemaphore = 1 << 7,
}

public enum ERvcExperimentalExtensionPolicy
{
    Disabled,
    CapabilityAdvertised,
    EnabledForPrototype,
}

public readonly record struct RvcFrameViewDiagnostics(
    uint ViewId,
    EVrOutputViewKind ViewKind,
    uint RuntimeWidth,
    uint RuntimeHeight,
    float HorizontalFovDegrees,
    float VerticalFovDegrees,
    ulong SwapchainIdentity,
    ulong PixelCount,
    double GpuMilliseconds,
    EVrViewRenderMode StereoMode,
    EVrFoveationMode FoveationMode,
    ERvcFallbackReason FallbackReason);

public readonly record struct RvcViewSetDiagnostics(
    int ViewCount,
    bool IsQuadViewSet,
    ulong TotalPixelCount,
    ERvcFallbackReason FallbackReason)
{
    public static RvcViewSetDiagnostics FromViewSet(in RenderFrameViewSet viewSet, ERvcFallbackReason fallbackReason = ERvcFallbackReason.None)
    {
        ulong pixels = 0UL;
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = viewSet.GetView(i);
            pixels += (ulong)view.ViewRect.Width * view.ViewRect.Height;
        }

        return new(viewSet.ViewCount, viewSet.IsQuadViewSet, pixels, fallbackReason);
    }
}

public readonly record struct RvcFrameViewProjectionDiagnostics(
    uint ViewId,
    int RuntimeViewIndex,
    int ViewportX,
    int ViewportY,
    int ViewportWidth,
    int ViewportHeight,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    Matrix4x4 ViewProjectionMatrix,
    Matrix4x4 PreviousViewProjectionMatrix)
{
    public static RvcFrameViewProjectionDiagnostics Empty => new(
        ViewId: 0u,
        RuntimeViewIndex: -1,
        ViewportX: 0,
        ViewportY: 0,
        ViewportWidth: 0,
        ViewportHeight: 0,
        Matrix4x4.Identity,
        Matrix4x4.Identity,
        Matrix4x4.Identity,
        Matrix4x4.Identity);
}

public readonly record struct RvcFrameProfileSnapshot(
    ulong FrameId,
    long PredictedDisplayTime,
    int ViewCount,
    RvcFrameViewDiagnostics View0,
    RvcFrameViewDiagnostics View1,
    RvcFrameViewDiagnostics View2,
    RvcFrameViewDiagnostics View3,
    RvcViewSetDiagnostics ViewSet,
    ERvcFallbackReason FallbackReason,
    string Diagnostic,
    RvcFrameViewProjectionDiagnostics Projection0,
    RvcFrameViewProjectionDiagnostics Projection1,
    RvcFrameViewProjectionDiagnostics Projection2,
    RvcFrameViewProjectionDiagnostics Projection3)
{
    public static RvcFrameProfileSnapshot Empty => default;

    public RvcFrameViewDiagnostics GetView(int index)
        => index switch
        {
            0 => View0,
            1 => View1,
            2 => View2,
            3 => View3,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "RVC frame profiles expose the first four XR views."),
        };

    public RvcFrameViewProjectionDiagnostics GetProjection(int index)
        => index switch
        {
            0 => Projection0,
            1 => Projection1,
            2 => Projection2,
            3 => Projection3,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "RVC frame profiles expose the first four XR views."),
        };

    public static RvcFrameProfileSnapshot Create(
        ulong frameId,
        long predictedDisplayTime,
        ReadOnlySpan<RvcFrameViewDiagnostics> views,
        ERvcFallbackReason fallbackReason,
        string diagnostic)
        => Create(
            frameId,
            predictedDisplayTime,
            views,
            ReadOnlySpan<RvcFrameViewProjectionDiagnostics>.Empty,
            fallbackReason,
            diagnostic);

    public static RvcFrameProfileSnapshot Create(
        ulong frameId,
        long predictedDisplayTime,
        ReadOnlySpan<RvcFrameViewDiagnostics> views,
        ReadOnlySpan<RvcFrameViewProjectionDiagnostics> projections,
        ERvcFallbackReason fallbackReason,
        string diagnostic)
    {
        int viewCount = Math.Min(views.Length, 4);
        RvcFrameViewDiagnostics view0 = viewCount > 0 ? views[0] : default;
        RvcFrameViewDiagnostics view1 = viewCount > 1 ? views[1] : default;
        RvcFrameViewDiagnostics view2 = viewCount > 2 ? views[2] : default;
        RvcFrameViewDiagnostics view3 = viewCount > 3 ? views[3] : default;
        int projectionCount = Math.Min(projections.Length, viewCount);
        RvcFrameViewProjectionDiagnostics projection0 = projectionCount > 0 ? projections[0] : RvcFrameViewProjectionDiagnostics.Empty;
        RvcFrameViewProjectionDiagnostics projection1 = projectionCount > 1 ? projections[1] : RvcFrameViewProjectionDiagnostics.Empty;
        RvcFrameViewProjectionDiagnostics projection2 = projectionCount > 2 ? projections[2] : RvcFrameViewProjectionDiagnostics.Empty;
        RvcFrameViewProjectionDiagnostics projection3 = projectionCount > 3 ? projections[3] : RvcFrameViewProjectionDiagnostics.Empty;
        ulong totalPixels = 0UL;
        for (int i = 0; i < viewCount; i++)
            totalPixels += views[i].PixelCount;

        bool quadViewSet =
            viewCount == 4 &&
            view0.ViewKind == EVrOutputViewKind.LeftWide &&
            view1.ViewKind == EVrOutputViewKind.RightWide &&
            view2.ViewKind == EVrOutputViewKind.LeftInset &&
            view3.ViewKind == EVrOutputViewKind.RightInset;

        return new(
            frameId,
            predictedDisplayTime,
            viewCount,
            view0,
            view1,
            view2,
            view3,
            new RvcViewSetDiagnostics(viewCount, quadViewSet, totalPixels, fallbackReason),
            fallbackReason,
            diagnostic,
            projection0,
            projection1,
            projection2,
            projection3);
    }
}

public readonly record struct RvcCounterReadbackContract(
    ERvcCounterReadbackMode Mode,
    int DelayFrames,
    bool DoubleBuffered,
    bool SynchronousReadbackForbidden)
{
    public static RvcCounterReadbackContract Default => new(
        ERvcCounterReadbackMode.DelayedGpuReadback,
        DelayFrames: 2,
        DoubleBuffered: true,
        SynchronousReadbackForbidden: true);

    public RvcDelayedCounterReadbackDecision Evaluate(
        ulong currentFrameId,
        ulong producedFrameId,
        bool synchronousReadbackRequested)
    {
        if (Mode == ERvcCounterReadbackMode.Disabled)
            return new(
                ERvcCounterReadbackDecision.Disabled,
                AllowReadback: false,
                EarliestReadableFrameId: producedFrameId,
                ERvcFallbackReason.None,
                "RVC counter readback is disabled.");

        if (synchronousReadbackRequested && SynchronousReadbackForbidden)
            return new(
                ERvcCounterReadbackDecision.SynchronousForbidden,
                AllowReadback: false,
                EarliestReadableFrameId: producedFrameId + (ulong)Math.Max(0, DelayFrames),
                ERvcFallbackReason.SynchronousCounterReadbackForbidden,
                "RVC forbids synchronous GPU counter readback on the render path.");

        ulong earliest = producedFrameId + (ulong)Math.Max(0, DelayFrames);
        bool ready = currentFrameId >= earliest;
        return new(
            ready ? ERvcCounterReadbackDecision.Ready : ERvcCounterReadbackDecision.Pending,
            ready,
            earliest,
            ERvcFallbackReason.None,
            ready
                ? "RVC delayed counter readback is ready."
                : "RVC delayed counter readback is still pending.");
    }
}

public readonly record struct RvcDelayedCounterReadbackDecision(
    ERvcCounterReadbackDecision Decision,
    bool AllowReadback,
    ulong EarliestReadableFrameId,
    ERvcFallbackReason FallbackReason,
    string Diagnostic);

public readonly record struct RvcOpenXrVisibilityMaskPlan(
    bool Requested,
    bool ExtensionEnabled,
    bool UseStencilPrepass,
    bool InvalidateOnRuntimeEvent,
    ERvcOpenXrVisibilityMaskStatus Status,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public bool CanStencil => Requested && ExtensionEnabled && UseStencilPrepass && FallbackReason == ERvcFallbackReason.None;

    public static RvcOpenXrVisibilityMaskPlan Resolve(bool requested, bool extensionEnabled)
    {
        if (!requested)
        {
            return new(
                Requested: false,
                ExtensionEnabled: extensionEnabled,
                UseStencilPrepass: false,
                InvalidateOnRuntimeEvent: false,
                ERvcOpenXrVisibilityMaskStatus.NotRequested,
                ERvcFallbackReason.None,
                "OpenXR visibility-mask stencil prepass is not requested.");
        }

        if (!extensionEnabled)
        {
            return new(
                Requested: true,
                ExtensionEnabled: false,
                UseStencilPrepass: false,
                InvalidateOnRuntimeEvent: false,
                ERvcOpenXrVisibilityMaskStatus.ExtensionMissing,
                ERvcFallbackReason.MissingVisibilityMask,
                "XR_KHR_visibility_mask is not enabled; hidden-area stencil prepass is unavailable.");
        }

        return new(
            Requested: true,
            ExtensionEnabled: true,
            UseStencilPrepass: true,
            InvalidateOnRuntimeEvent: true,
            ERvcOpenXrVisibilityMaskStatus.AwaitingRuntimeMesh,
            ERvcFallbackReason.None,
            "XR_KHR_visibility_mask is enabled; hidden-area meshes can feed the RVC stencil prepass.");
    }
}

public readonly record struct RvcOpenXrVisibilityMaskState(
    uint ViewIndex,
    EVrOutputViewKind ViewKind,
    bool HiddenAreaMeshAvailable,
    bool VisibleAreaMeshAvailable,
    uint HiddenAreaVertexCount,
    uint HiddenAreaIndexCount,
    uint VisibleAreaVertexCount,
    uint VisibleAreaIndexCount,
    ulong Revision,
    ERvcOpenXrVisibilityMaskStatus Status,
    string Diagnostic)
{
    public bool CanUseHiddenAreaStencil => HiddenAreaMeshAvailable && Status == ERvcOpenXrVisibilityMaskStatus.ReadyForStencilPrepass;

    public RvcOpenXrVisibilityMaskState MarkInvalidated(string diagnostic)
        => this with
        {
            Revision = Revision + 1UL,
            Status = ERvcOpenXrVisibilityMaskStatus.InvalidatedByRuntime,
            Diagnostic = diagnostic,
        };
}

public readonly record struct RvcQualitySettings(
    float FovealRadiusDegrees,
    float GuardBandDegrees,
    float MidFieldRadiusDegrees,
    ERvcShadeletDensity PeripheralMaxRate,
    float ForceFullResNearDistanceMeters,
    ERvcDerivativeStrategy DerivativeStrategy,
    ERvcFovealAntiAliasingPath FovealAntiAliasingPath,
    float ReuseMaxNormalAngleDegrees,
    float ReuseMaxDepthDeltaMeters,
    byte ReuseMaxRoughnessBucketDelta)
{
    public static RvcQualitySettings Defaults => new(
        FovealRadiusDegrees: 5.0f,
        GuardBandDegrees: 8.0f,
        MidFieldRadiusDegrees: 30.0f,
        ERvcShadeletDensity.Rate4x4,
        ForceFullResNearDistanceMeters: 1.5f,
        ERvcDerivativeStrategy.AnalyticFromVisibilityGradients,
        ERvcFovealAntiAliasingPath.VisibilityEdgeAA,
        ReuseMaxNormalAngleDegrees: 5.0f,
        ReuseMaxDepthDeltaMeters: 0.05f,
        ReuseMaxRoughnessBucketDelta: 1);
}

public readonly record struct RvcResourceBarrierContract(
    ERvcResourceBarrierBackend Backend,
    ERvcFrameGraphUsage BeforeUsage,
    ERvcFrameGraphUsage AfterUsage,
    string ResourceName,
    string DiagnosticName);

public readonly record struct RvcResourceAliasContract(
    string FirstResourceName,
    string SecondResourceName,
    ERvcResourceAliasRule Rules)
{
    public bool CanAlias(bool sameView, bool hasHistory, bool isDebugOrMirror, bool isExternalSwapchain)
    {
        if ((Rules & ERvcResourceAliasRule.SameViewOnly) != 0 && !sameView)
            return false;
        if ((Rules & ERvcResourceAliasRule.NoHistoryAlias) != 0 && hasHistory)
            return false;
        if ((Rules & ERvcResourceAliasRule.DebugResourcesNeverAlias) != 0 && isDebugOrMirror)
            return false;
        if ((Rules & ERvcResourceAliasRule.ExternalSwapchainNeverAlias) != 0 && isExternalSwapchain)
            return false;

        return true;
    }
}

public readonly record struct RvcVisibilityTargetContract(
    ERvcVisibilityPayloadFormat PayloadFormat,
    ERvcVisibilityExecutionLane AllowedLanes,
    bool PerViewDepthTarget,
    bool PerViewVisibilityTarget,
    bool PerViewVelocityTarget,
    bool BackendNeutralIdentity)
{
    public static RvcVisibilityTargetContract Default => new(
        ERvcVisibilityPayloadFormat.Rg32UintIdentity64,
        ERvcVisibilityExecutionLane.HardwareRaster |
        ERvcVisibilityExecutionLane.MeshletCompute |
        ERvcVisibilityExecutionLane.MeshShader |
        ERvcVisibilityExecutionLane.ForwardPlusFallback,
        PerViewDepthTarget: true,
        PerViewVisibilityTarget: true,
        PerViewVelocityTarget: true,
        BackendNeutralIdentity: true);
}

public readonly record struct RvcAttributeReconstructionContract(
    bool Position,
    bool Normal,
    bool Tangent,
    bool Uv,
    bool MaterialRow,
    bool PreviousPosition,
    bool Velocity)
{
    public static RvcAttributeReconstructionContract Full => new(
        Position: true,
        Normal: true,
        Tangent: true,
        Uv: true,
        MaterialRow: true,
        PreviousPosition: true,
        Velocity: true);
}

public readonly record struct RvcHzbRejectionContract(
    bool PreviousDepthMayReject,
    bool RequiresSafeReprojection,
    bool RequiresStaticOrValidatedDynamicObject,
    bool RequiresCurrentFramePostValidation,
    float EdgeDepthAgreementMeters)
{
    public static RvcHzbRejectionContract Conservative => new(
        PreviousDepthMayReject: true,
        RequiresSafeReprojection: true,
        RequiresStaticOrValidatedDynamicObject: true,
        RequiresCurrentFramePostValidation: true,
        EdgeDepthAgreementMeters: 0.01f);
}

public readonly record struct RvcVisibilityPassPlan(
    RvcVisibilityTargetContract Targets,
    RvcAttributeReconstructionContract Reconstruction,
    RvcHzbRejectionContract Hzb,
    ERvcVisibilityExecutionLane PrimaryLane,
    ERvcVisibilityExecutionLane FallbackLane,
    bool RenderWideBeforeInset,
    bool SeedInsetHzbFromWideDepth)
{
    public static RvcVisibilityPassPlan Default => new(
        RvcVisibilityTargetContract.Default,
        RvcAttributeReconstructionContract.Full,
        RvcHzbRejectionContract.Conservative,
        ERvcVisibilityExecutionLane.HardwareRaster,
        ERvcVisibilityExecutionLane.ForwardPlusFallback,
        RenderWideBeforeInset: true,
        SeedInsetHzbFromWideDepth: true);
}

public readonly record struct RvcVisibilitySourcePathPlan(
    ERvcVisibilitySourcePath EnabledPaths,
    ERvcVisibilitySourcePath RequiredPaths,
    bool StaticMeshPathUsesDirectDrawIdentity,
    bool SkinnedPathConsumesComputeOutput,
    bool ZeroReadbackMaterialRowsRequired,
    bool MeshletPathOptional,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public bool HasRequiredPaths => (EnabledPaths & RequiredPaths) == RequiredPaths;
    public bool HasGpuCacheSource => (EnabledPaths & ~ERvcVisibilitySourcePath.ForwardPlusOracle) != 0;

    public static RvcVisibilitySourcePathPlan Resolve(
        in RvcPipelineResolution resolution,
        bool staticMeshPathAvailable,
        bool skinnedComputePathAvailable,
        bool zeroReadbackMaterialRowsAvailable,
        bool meshletPathAvailable)
    {
        ERvcVisibilitySourcePath enabled = ERvcVisibilitySourcePath.ForwardPlusOracle;
        if (staticMeshPathAvailable)
            enabled |= ERvcVisibilitySourcePath.StaticMeshDirect;
        if (skinnedComputePathAvailable)
            enabled |= ERvcVisibilitySourcePath.SkinnedComputeOutput;
        if (zeroReadbackMaterialRowsAvailable)
            enabled |= ERvcVisibilitySourcePath.ZeroReadbackMaterialTable;
        if (meshletPathAvailable)
            enabled |= ERvcVisibilitySourcePath.MeshletTaskExpansion;

        ERvcVisibilitySourcePath required = resolution.IsRvcActive
            ? ERvcVisibilitySourcePath.StaticMeshDirect | ERvcVisibilitySourcePath.ZeroReadbackMaterialTable
            : ERvcVisibilitySourcePath.ForwardPlusOracle;

        bool hasRequired = (enabled & required) == required;
        return new(
            enabled,
            required,
            StaticMeshPathUsesDirectDrawIdentity: staticMeshPathAvailable,
            SkinnedPathConsumesComputeOutput: skinnedComputePathAvailable,
            ZeroReadbackMaterialRowsRequired: resolution.IsRvcActive,
            MeshletPathOptional: true,
            hasRequired ? ERvcFallbackReason.None : ERvcFallbackReason.MissingVisibilitySourcePath,
            hasRequired
                ? "RVC visibility source paths are available for the resolved mode."
                : "RVC requires static mesh identity and zero-readback material rows before cache passes can run.");
    }
}

public readonly record struct RvcGpuPassExecutionPlan(
    ERvcGpuPassStage PlannedStages,
    ERvcGpuPassStage BackendImplementedStages,
    ERvcGpuPassStage ForwardPlusFallbackStages,
    bool UsesFrameGraphResources,
    bool UsesDelayedCounterReadback,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public ERvcGpuPassStage MissingBackendStages => PlannedStages & ~BackendImplementedStages;
    public bool IsFullyImplemented => MissingBackendStages == ERvcGpuPassStage.None && FallbackReason == ERvcFallbackReason.None;

    public static RvcGpuPassExecutionPlan FromPlan(
        in RvcPipelineResolution resolution,
        bool visibilityTargetsImplemented,
        bool materialCacheImplemented,
        bool sharedLightingImplemented,
        bool temporalResolveImplemented,
        bool diagnosticOverlayImplemented)
    {
        ERvcGpuPassStage planned = ResolvePlannedStages(resolution.RequestedMode);
        ERvcGpuPassStage implemented = ERvcGpuPassStage.None;

        if (visibilityTargetsImplemented)
        {
            implemented |=
                ERvcGpuPassStage.VisibilityTargets |
                ERvcGpuPassStage.OpenXrVisibilityMaskStencil |
                ERvcGpuPassStage.AttributeReconstruction |
                ERvcGpuPassStage.HzbRejection;
        }

        if (materialCacheImplemented)
        {
            implemented |=
                ERvcGpuPassStage.PixelToShadeletMap |
                ERvcGpuPassStage.MaterialShadeletShading |
                ERvcGpuPassStage.FoveatedShadingRate |
                ERvcGpuPassStage.ReuseValidation;
        }

        if (sharedLightingImplemented)
        {
            implemented |=
                ERvcGpuPassStage.HeadSpaceLightClusters |
                ERvcGpuPassStage.SharedLighting;
        }

        if (temporalResolveImplemented)
        {
            implemented |=
                ERvcGpuPassStage.TemporalCache |
                ERvcGpuPassStage.FoveatedResolve |
                ERvcGpuPassStage.TransparencyForwardPlus;
        }

        if (diagnosticOverlayImplemented)
            implemented |= ERvcGpuPassStage.DiagnosticOverlay;

        ERvcGpuPassStage missing = planned & ~implemented;
        bool cacheRequested = resolution.RequestedMode is not ERvcPipelineMode.Off and not ERvcPipelineMode.ForwardPlusOracle;
        return new(
            planned,
            implemented,
            cacheRequested ? missing : ERvcGpuPassStage.None,
            UsesFrameGraphResources: cacheRequested,
            UsesDelayedCounterReadback: cacheRequested,
            cacheRequested && missing != ERvcGpuPassStage.None
                ? (resolution.FallbackReason == ERvcFallbackReason.None ? ERvcFallbackReason.MissingVisibilityTargets : resolution.FallbackReason)
                : ERvcFallbackReason.None,
            cacheRequested && missing != ERvcGpuPassStage.None
                ? "One or more RVC GPU pass stages still lack a backend implementation."
                : "RVC GPU pass execution plan is resolved.");
    }

    private static ERvcGpuPassStage ResolvePlannedStages(ERvcPipelineMode mode)
        => mode switch
        {
            ERvcPipelineMode.VisibilityOnlyDebug =>
                ERvcGpuPassStage.VisibilityTargets |
                ERvcGpuPassStage.OpenXrVisibilityMaskStencil |
                ERvcGpuPassStage.AttributeReconstruction |
                ERvcGpuPassStage.HzbRejection |
                ERvcGpuPassStage.DiagnosticOverlay,
            ERvcPipelineMode.MaterialCache =>
                ResolvePlannedStages(ERvcPipelineMode.VisibilityOnlyDebug) |
                ERvcGpuPassStage.PixelToShadeletMap |
                ERvcGpuPassStage.MaterialShadeletShading |
                ERvcGpuPassStage.FoveatedShadingRate |
                ERvcGpuPassStage.ReuseValidation,
            ERvcPipelineMode.SharedLighting =>
                ResolvePlannedStages(ERvcPipelineMode.MaterialCache) |
                ERvcGpuPassStage.HeadSpaceLightClusters |
                ERvcGpuPassStage.SharedLighting,
            ERvcPipelineMode.Full =>
                ResolvePlannedStages(ERvcPipelineMode.SharedLighting) |
                ERvcGpuPassStage.TemporalCache |
                ERvcGpuPassStage.FoveatedResolve |
                ERvcGpuPassStage.TransparencyForwardPlus,
            _ => ERvcGpuPassStage.None,
        };
}

public readonly record struct RvcVulkanProductionPlan(
    ERvcVulkanProductionFeature RequiredFeatures,
    ERvcVulkanProductionFeature AvailableFeatures,
    bool OpenGlCorrectnessSliceOnly,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public ERvcVulkanProductionFeature MissingFeatures => RequiredFeatures & ~AvailableFeatures;
    public bool IsProductionReady => !OpenGlCorrectnessSliceOnly && MissingFeatures == ERvcVulkanProductionFeature.None;

    public static RvcVulkanProductionPlan Resolve(
        in RvcPipelineResolution resolution,
        in RvcCapabilityMatrix capabilities,
        bool dynamicRenderingAvailable,
        bool synchronization2Available,
        bool meshShaderAvailable,
        bool timelineSemaphoreAvailable)
    {
        ERvcVulkanProductionFeature required = ERvcVulkanProductionFeature.None;
        if (resolution.EffectiveMode == ERvcPipelineMode.Full)
        {
            required =
                ERvcVulkanProductionFeature.DynamicRendering |
                ERvcVulkanProductionFeature.Synchronization2 |
                ERvcVulkanProductionFeature.DescriptorIndexing |
                ERvcVulkanProductionFeature.TimelineSemaphore;

            if (capabilities.MultiviewSupported)
                required |= ERvcVulkanProductionFeature.Multiview;
            if (capabilities.FragmentShadingRateSupported)
                required |= ERvcVulkanProductionFeature.FragmentShadingRate;
            if (capabilities.FragmentDensityMapSupported)
                required |= ERvcVulkanProductionFeature.FragmentDensityMap;
            if (meshShaderAvailable)
                required |= ERvcVulkanProductionFeature.MeshShader;
        }

        ERvcVulkanProductionFeature available = ERvcVulkanProductionFeature.None;
        if (capabilities.MultiviewSupported)
            available |= ERvcVulkanProductionFeature.Multiview;
        if (dynamicRenderingAvailable)
            available |= ERvcVulkanProductionFeature.DynamicRendering;
        if (synchronization2Available)
            available |= ERvcVulkanProductionFeature.Synchronization2;
        if (capabilities.DescriptorIndexingSupported)
            available |= ERvcVulkanProductionFeature.DescriptorIndexing;
        if (capabilities.FragmentShadingRateSupported)
            available |= ERvcVulkanProductionFeature.FragmentShadingRate;
        if (capabilities.FragmentDensityMapSupported)
            available |= ERvcVulkanProductionFeature.FragmentDensityMap;
        if (meshShaderAvailable)
            available |= ERvcVulkanProductionFeature.MeshShader;
        if (timelineSemaphoreAvailable)
            available |= ERvcVulkanProductionFeature.TimelineSemaphore;

        bool openGlSlice = capabilities.OpenGlBackend && !capabilities.VulkanBackend;
        ERvcVulkanProductionFeature missing = required & ~available;
        return new(
            required,
            available,
            openGlSlice,
            missing == ERvcVulkanProductionFeature.None ? ERvcFallbackReason.None : ERvcFallbackReason.MissingVulkanProductionFeature,
            missing == ERvcVulkanProductionFeature.None
                ? "RVC Vulkan production feature plan is satisfied by the declared capabilities."
                : "RVC Vulkan production feature plan is missing one or more required features.");
    }
}

public readonly record struct RvcExperimentalExtensionPlan(
    ERvcExperimentalExtensionPolicy FragmentDensityMap,
    ERvcExperimentalExtensionPolicy MeshShader,
    ERvcExperimentalExtensionPolicy EyeTrackedFoveation,
    ERvcExperimentalExtensionPolicy PeripheralCheckerboard,
    string Diagnostic)
{
    public static RvcExperimentalExtensionPlan Resolve(
        in RvcCapabilityMatrix capabilities,
        bool meshShaderAvailable,
        bool allowPrototypeExtensions)
    {
        ERvcExperimentalExtensionPolicy Policy(bool available)
        {
            if (!available)
                return ERvcExperimentalExtensionPolicy.Disabled;
            return allowPrototypeExtensions
                ? ERvcExperimentalExtensionPolicy.EnabledForPrototype
                : ERvcExperimentalExtensionPolicy.CapabilityAdvertised;
        }

        return new(
            Policy(capabilities.FragmentDensityMapSupported),
            Policy(meshShaderAvailable),
            Policy(capabilities.OpenXrRuntimeFoveationSupported || capabilities.OpenXrQuadViewsSupported),
            allowPrototypeExtensions
                ? ERvcExperimentalExtensionPolicy.EnabledForPrototype
                : ERvcExperimentalExtensionPolicy.Disabled,
            allowPrototypeExtensions
                ? "RVC prototype-only extension paths may be selected by diagnostics."
                : "RVC experimental extension paths are advertised but disabled for production defaults.");
    }
}

public readonly record struct RvcVisibilityCounters(
    ulong VisiblePixels,
    ulong CulledCandidates,
    ulong UncertainCandidates,
    ulong PostValidationCandidates,
    ulong PageRequests,
    ulong HardwareRasterCandidates,
    ulong MeshletCandidates,
    ulong SoftwareRasterCandidates)
{
    public static RvcVisibilityCounters Empty => default;
}

public static class RvcMaterialClassifier
{
    public static ERvcMaterialClass Classify(
        bool transparent,
        bool refractiveOrOrderDependent,
        bool expensiveAlphaTest,
        bool generatedMaterialTableOpaque,
        bool unlit,
        bool pbr)
    {
        if (transparent || refractiveOrOrderDependent)
            return ERvcMaterialClass.TransparentForwardPlusFallback;
        if (expensiveAlphaTest)
            return ERvcMaterialClass.TransparentForwardPlusFallback;
        if (generatedMaterialTableOpaque)
            return ERvcMaterialClass.GeneratedMaterialTableOpaque;
        if (pbr)
            return ERvcMaterialClass.OpaquePbr;
        if (unlit)
            return ERvcMaterialClass.UnlitOpaque;

        return ERvcMaterialClass.Unsupported;
    }
}

public readonly record struct RvcPixelToShadeletEncoding(
    int TileWidth,
    int TileHeight,
    int LocalIndexBits,
    string TileBaseFormat,
    string LocalIndexFormat)
{
    public static RvcPixelToShadeletEncoding Default => new(
        TileWidth: 8,
        TileHeight: 8,
        LocalIndexBits: 16,
        TileBaseFormat: "R32_UINT",
        LocalIndexFormat: "R16_UINT");
}

public readonly record struct RvcShadeletMapBudget(
    uint MaxShadeletsPerView,
    uint MaxShadeletsPerTile,
    uint MaxMaterialBins,
    uint MaxGlobalDedupSurvivors)
{
    public static RvcShadeletMapBudget Default => new(
        MaxShadeletsPerView: 4_194_304u,
        MaxShadeletsPerTile: 64u,
        MaxMaterialBins: 4096u,
        MaxGlobalDedupSurvivors: 4_194_304u);

    public ERvcFallbackReason Check(uint shadeletsPerView, uint shadeletsPerTile, uint materialBins, uint globalSurvivors)
    {
        if (shadeletsPerView > MaxShadeletsPerView || shadeletsPerTile > MaxShadeletsPerTile)
            return ERvcFallbackReason.ShadeletMapOverflow;
        if (materialBins > MaxMaterialBins || globalSurvivors > MaxGlobalDedupSurvivors)
            return ERvcFallbackReason.DeduplicationOverflow;

        return ERvcFallbackReason.None;
    }
}

public readonly record struct RvcShadeletDensityPolicy(
    ERvcShadeletDensity Fovea,
    ERvcShadeletDensity GuardBand,
    ERvcShadeletDensity MidField,
    ERvcShadeletDensity Periphery,
    bool ForceNearFieldAndUiTo1x1,
    float FullResNearDistanceMeters)
{
    public static RvcShadeletDensityPolicy Default => new(
        ERvcShadeletDensity.Rate1x1,
        ERvcShadeletDensity.Rate1x1,
        ERvcShadeletDensity.Rate2x2,
        ERvcShadeletDensity.Rate4x4,
        ForceNearFieldAndUiTo1x1: true,
        FullResNearDistanceMeters: 1.5f);

    public ERvcShadeletDensity Resolve(ERvcFoveationRegion region, bool isUiOrHand, float distanceMeters)
    {
        if (ForceNearFieldAndUiTo1x1 && (isUiOrHand || distanceMeters <= FullResNearDistanceMeters))
            return ERvcShadeletDensity.Rate1x1;

        return region switch
        {
            ERvcFoveationRegion.Foveal => Fovea,
            ERvcFoveationRegion.GuardBand => GuardBand,
            ERvcFoveationRegion.MidField => MidField,
            _ => Periphery,
        };
    }
}

public readonly record struct RvcShadeletDeduplicationPlan(
    bool TileLocalSharedMemoryDedup,
    bool GlobalMergeTileSurvivors,
    bool SortOrBinByMaterial,
    RvcPixelToShadeletEncoding PixelMapEncoding,
    RvcShadeletMapBudget Budget)
{
    public static RvcShadeletDeduplicationPlan Default => new(
        TileLocalSharedMemoryDedup: true,
        GlobalMergeTileSurvivors: true,
        SortOrBinByMaterial: true,
        RvcPixelToShadeletEncoding.Default,
        RvcShadeletMapBudget.Default);
}

public readonly record struct RvcFoveatedShadingRatePlan(
    ERvcFoveationRateBackend Backend,
    ERvcShadeletDensity MaxFragmentShadingRateDensity,
    bool RequiresComputeFor8x8,
    bool UsesCombinerOpsForNearField)
{
    public static RvcFoveatedShadingRatePlan FromBackend(ERvcFoveationRateBackend backend)
        => new(
            backend,
            MaxFragmentShadingRateDensity: ERvcShadeletDensity.Rate4x4,
            RequiresComputeFor8x8: true,
            UsesCombinerOpsForNearField: backend == ERvcFoveationRateBackend.VulkanFragmentShadingRate);
}

public readonly record struct RvcWideInsetCompositionPolicy(
    ERvcShadeletDensity WideUnderInsetDensity,
    float BlendGuardBandDegrees,
    bool DisablePerViewSpecularUnderInset,
    bool WideViewOnlyNeedsPlausibleBlendData)
{
    public static RvcWideInsetCompositionPolicy Default => new(
        ERvcShadeletDensity.Rate8x8,
        BlendGuardBandDegrees: 1.0f,
        DisablePerViewSpecularUnderInset: true,
        WideViewOnlyNeedsPlausibleBlendData: true);
}

public readonly record struct RvcMaterialResourceRow(
    uint MaterialRowId,
    uint ResourceGeneration,
    uint BaseColorResourceIndex,
    uint NormalResourceIndex,
    uint RoughnessMetallicResourceIndex,
    uint SamplerIndex)
{
    public bool IsSameResourceGeneration(in RvcShadeletRecord shadelet)
        => shadelet.MaterialRowId == MaterialRowId &&
           shadelet.MaterialResourceGeneration == ResourceGeneration;
}

public readonly record struct RvcMaterialBindingContract(
    ERvcDescriptorBackend SelectedBackend,
    bool DescriptorHeapPreferred,
    bool DescriptorIndexingRowsSemanticallyIdentical,
    bool ShadeletKeysExcludeBackendDescriptorHandles)
{
    public static RvcMaterialBindingContract FromResolution(in RvcPipelineResolution resolution)
        => new(
            resolution.DescriptorBackend,
            DescriptorHeapPreferred: true,
            DescriptorIndexingRowsSemanticallyIdentical: true,
            ShadeletKeysExcludeBackendDescriptorHandles: true);
}

public readonly record struct RvcMaterialBinKey(
    uint MaterialRowId,
    uint MaterialResourceGeneration,
    ERvcMaterialClass MaterialClass);

public readonly record struct RvcShadeletTelemetryCounters(
    ulong UniqueShadelets,
    ulong IntraViewKeyMatches,
    ulong InsetWideKeyMatches,
    ulong StereoKeyMatches,
    ulong MaterialBinCount,
    ulong CacheMisses,
    ulong ShadeletMapOverflows,
    ulong DeduplicationOverflows);

public readonly record struct RvcEdgeRejectionPolicy(
    ERvcEdgeRejectionFlags Flags,
    float MaxDepthDeltaMeters,
    float MaxNormalAngleDegrees)
{
    public static RvcEdgeRejectionPolicy Conservative => new(
        ERvcEdgeRejectionFlags.Depth |
        ERvcEdgeRejectionFlags.Normal |
        ERvcEdgeRejectionFlags.Material |
        ERvcEdgeRejectionFlags.Primitive |
        ERvcEdgeRejectionFlags.Disocclusion,
        MaxDepthDeltaMeters: 0.02f,
        MaxNormalAngleDegrees: 8.0f);
}

public readonly record struct RvcFoveationLightBudget(
    int FovealExactLights,
    int GuardBandExactLights,
    int MidFieldExactLights,
    int PeripheryExactLights,
    int PeripheryAggregateLights)
{
    public static RvcFoveationLightBudget Default => new(
        FovealExactLights: 64,
        GuardBandExactLights: 48,
        MidFieldExactLights: 24,
        PeripheryExactLights: 8,
        PeripheryAggregateLights: 4);

    public int ExactLightBudgetForRegion(ERvcFoveationRegion region)
        => region switch
        {
            ERvcFoveationRegion.Foveal => FovealExactLights,
            ERvcFoveationRegion.GuardBand => GuardBandExactLights,
            ERvcFoveationRegion.MidField => MidFieldExactLights,
            _ => PeripheryExactLights,
        };
}

public readonly record struct RvcLightClusterGridDescriptor(
    ERvcLightGridSpace Space,
    Vector3 CameraRelativeOrigin,
    Vector3 ExtentsMeters,
    float CellSizeMeters,
    RvcFoveationLightBudget LightBudget)
{
    public static RvcLightClusterGridDescriptor CreateDefault(Vector3 cameraRelativeOrigin)
        => new(
            ERvcLightGridSpace.WorldAlignedCameraRelative,
            cameraRelativeOrigin,
            new Vector3(64.0f, 32.0f, 64.0f),
            CellSizeMeters: 0.5f,
            RvcFoveationLightBudget.Default);
}

public readonly record struct RvcLightReference(
    uint LightId,
    uint ResourceIndex,
    float EstimatedContribution,
    bool CastsShadow,
    bool UsesCookie);

public readonly record struct RvcLightClusterRecord(
    RvcHeadSpaceClusterKey Key,
    uint FirstLightIndex,
    uint LightCount,
    RvcLightReservoir Reservoir);

public readonly record struct RvcAggregateLight(
    Vector3 DirectionOrPosition,
    Vector3 Radiance,
    float WeightSum,
    uint SourceLightCount)
{
    public static RvcAggregateLight Empty => default;

    public RvcAggregateLight Add(Vector3 directionOrPosition, Vector3 radiance, float weight)
    {
        float clampedWeight = MathF.Max(0.0f, weight);
        float nextWeight = WeightSum + clampedWeight;
        Vector3 nextDirection = nextWeight > 0.0f
            ? ((DirectionOrPosition * WeightSum) + (directionOrPosition * clampedWeight)) / nextWeight
            : Vector3.Zero;
        Vector3 nextRadiance = Radiance + radiance * clampedWeight;
        return new(nextDirection, nextRadiance, nextWeight, SourceLightCount + 1u);
    }
}

public readonly record struct RvcSharedLightingPlan(
    bool UseExistingForwardPlusLightMetadata,
    bool HeapBackedResourceReferences,
    bool KeepPerViewForwardPlusTileGridFallback,
    bool ReservoirEvaluationEnabled,
    RvcLightClusterGridDescriptor ClusterGrid)
{
    public static RvcSharedLightingPlan CreateDefault(Vector3 cameraRelativeOrigin)
        => new(
            UseExistingForwardPlusLightMetadata: true,
            HeapBackedResourceReferences: true,
            KeepPerViewForwardPlusTileGridFallback: true,
            ReservoirEvaluationEnabled: true,
            RvcLightClusterGridDescriptor.CreateDefault(cameraRelativeOrigin));
}

public readonly record struct RvcSharedLightingCounters(
    ulong ClusterCount,
    ulong ExactLightReferences,
    ulong RejectedLightReferences,
    ulong AggregateLightCount,
    ulong ReservoirCandidateCount,
    float EstimatedEnergyError);

public readonly record struct RvcReusePolicy(
    ERvcReuseDomain EnabledDomains,
    float MaxNormalAngleDegrees,
    float MaxDepthDeltaMeters,
    byte MaxRoughnessBucketDelta,
    byte MaxLodBucketDelta,
    ERvcMaterialViewDependency ExcludedViewDependencies)
{
    public static RvcReusePolicy DefaultsWithStereoOff => new(
        ERvcReuseDomain.IntraView | ERvcReuseDomain.InsetWide,
        MaxNormalAngleDegrees: 5.0f,
        MaxDepthDeltaMeters: 0.05f,
        MaxRoughnessBucketDelta: 1,
        MaxLodBucketDelta: 0,
        ERvcMaterialViewDependency.Refraction |
        ERvcMaterialViewDependency.ParallaxOcclusion |
        ERvcMaterialViewDependency.VirtualDisplacement |
        ERvcMaterialViewDependency.ScreenSpaceEffect);

    public bool IsEnabled(ERvcReuseDomain domain)
        => (EnabledDomains & domain) != 0;
}

public readonly record struct RvcReuseCounters(
    ulong IntraViewReuseAttempts,
    ulong InsetWideReuseAttempts,
    ulong StereoReuseAttempts,
    ulong TemporalReuseAttempts,
    ulong AcceptedReuse,
    ulong RejectedReuse,
    ulong DisocclusionLocalShading);

public readonly record struct RvcReuseValidationPlan(
    RvcReusePolicy Policy,
    RvcEdgeRejectionPolicy EdgeRejection,
    bool RequireAbHarnessBeforeStereoDefault,
    bool KeepSharpSpecularPerView)
{
    public static RvcReuseValidationPlan Defaults => new(
        RvcReusePolicy.DefaultsWithStereoOff,
        RvcEdgeRejectionPolicy.Conservative,
        RequireAbHarnessBeforeStereoDefault: true,
        KeepSharpSpecularPerView: true);
}

public readonly record struct RvcTemporalCacheEntry(
    RvcTemporalHashGridKey Key,
    RvcShadeletKey ShadeletKey,
    uint MaterialResourceGeneration,
    uint DeformationVersion,
    byte LodBucket,
    byte Confidence,
    ushort AgeFrames,
    ERvcTemporalInvalidationReason InvalidationReason)
{
    public bool IsValid => InvalidationReason == ERvcTemporalInvalidationReason.None && Confidence > 0;
}

public readonly record struct RvcTemporalCachePolicy(
    bool PersistentStaticDiffuseEntries,
    bool WorldSpaceHashGrid,
    ushort MaxAgeFrames,
    byte MinConfidenceForReuse)
{
    public static RvcTemporalCachePolicy Default => new(
        PersistentStaticDiffuseEntries: true,
        WorldSpaceHashGrid: true,
        MaxAgeFrames: 60,
        MinConfidenceForReuse: 128);
}

public readonly record struct RvcLateLatchFoveationConstants(
    Vector2 FoveationCenterUv,
    Vector2 GuardBandRadiiUv,
    ulong DeviceAddress,
    bool WrittenAtSubmitTime,
    bool RebuildsCommandBuffers)
{
    public static RvcLateLatchFoveationConstants Disabled => new(
        new Vector2(0.5f, 0.5f),
        Vector2.Zero,
        DeviceAddress: 0UL,
        WrittenAtSubmitTime: false,
        RebuildsCommandBuffers: false);
}

public readonly record struct RvcSaccadeQualityPolicy(
    bool Enabled,
    float VelocityThresholdDegreesPerSecond,
    ERvcShadeletDensity DuringSaccadeMaxDensity,
    bool LandQualityAtPredictedEndpoint)
{
    public static RvcSaccadeQualityPolicy ConservativeDisabled => new(
        Enabled: false,
        VelocityThresholdDegreesPerSecond: 300.0f,
        ERvcShadeletDensity.Rate4x4,
        LandQualityAtPredictedEndpoint: true);
}

public readonly record struct RvcFoveatedResolveContract(
    ERvcFovealAntiAliasingPath FovealAntiAliasingPath,
    bool FoveatedTaaFallbackAvailable,
    bool EdgeAwareUpsamplingUnderstandsViewIdentity,
    bool WideInsetMirrorComposition)
{
    public static RvcFoveatedResolveContract Default => new(
        ERvcFovealAntiAliasingPath.VisibilityEdgeAA,
        FoveatedTaaFallbackAvailable: true,
        EdgeAwareUpsamplingUnderstandsViewIdentity: true,
        WideInsetMirrorComposition: true);
}

public readonly record struct RvcTemporalCounters(
    ulong CacheHits,
    ulong CacheMisses,
    ulong InvalidatedEntries,
    ulong FovealStaleRejections,
    float TemporalHitRate);

public readonly record struct RvcFrameCounters(
    RvcVisibilityCounters Visibility,
    RvcShadeletTelemetryCounters Shadelets,
    RvcSharedLightingCounters SharedLighting,
    RvcReuseCounters Reuse,
    RvcTemporalCounters Temporal)
{
    public static RvcFrameCounters Empty => default;
}

public readonly record struct RvcDiagnosticsSnapshot(
    RvcPipelineResolution Resolution,
    RvcViewSetDiagnostics ViewSet,
    RvcFrameCounters Counters,
    ERvcDebugViewMode DebugViewMode,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public static RvcDiagnosticsSnapshot FromPlan(
        in RvcPipelinePlan plan,
        in RvcViewSetDiagnostics viewSet,
        in RvcFrameCounters counters)
        => new(
            plan.Resolution,
            viewSet,
            counters,
            plan.Settings.DebugViewMode,
            plan.Resolution.FallbackReason,
            plan.Resolution.Diagnostic);
}

public readonly record struct RvcPipelinePlan(
    RvcRenderingSettings Settings,
    RvcQualitySettings Quality,
    RvcPipelineResolution Resolution,
    RvcCapabilityMatrix Capabilities,
    RvcCounterReadbackContract CounterReadback,
    RvcVisibilityPassPlan Visibility,
    RvcShadeletDensityPolicy ShadeletDensity,
    RvcShadeletDeduplicationPlan ShadeletDeduplication,
    RvcFoveatedShadingRatePlan FoveatedShadingRate,
    RvcWideInsetCompositionPolicy WideInsetComposition,
    RvcMaterialBindingContract MaterialBinding,
    RvcSharedLightingPlan SharedLighting,
    RvcReuseValidationPlan Reuse,
    RvcTemporalCachePolicy TemporalCache,
    RvcFoveatedResolveContract Resolve,
    RvcOpenXrVisibilityMaskPlan OpenXrVisibilityMask,
    RvcVisibilitySourcePathPlan VisibilitySourcePaths,
    RvcGpuPassExecutionPlan GpuPassExecution,
    RvcVulkanProductionPlan VulkanProduction,
    RvcExperimentalExtensionPlan ExperimentalExtensions)
{
    public bool UsesForwardPlusOracle => Resolution.EffectiveMode == ERvcPipelineMode.ForwardPlusOracle;

    public static RvcPipelinePlan Build(
        in RvcRenderingSettings settings,
        in RvcQualitySettings quality,
        in RvcPipelineResolution resolution,
        in RvcCapabilityMatrix capabilities)
        => new(
            settings,
            quality,
            resolution,
            capabilities,
            RvcCounterReadbackContract.Default,
            RvcVisibilityPassPlan.Default,
            new RvcShadeletDensityPolicy(
                ERvcShadeletDensity.Rate1x1,
                ERvcShadeletDensity.Rate1x1,
                ERvcShadeletDensity.Rate2x2,
                quality.PeripheralMaxRate,
                ForceNearFieldAndUiTo1x1: true,
                quality.ForceFullResNearDistanceMeters),
            RvcShadeletDeduplicationPlan.Default,
            RvcFoveatedShadingRatePlan.FromBackend(resolution.FoveationRateBackend),
            RvcWideInsetCompositionPolicy.Default,
            RvcMaterialBindingContract.FromResolution(resolution),
            RvcSharedLightingPlan.CreateDefault(Vector3.Zero),
            new RvcReuseValidationPlan(
                RvcReusePolicy.DefaultsWithStereoOff with
                {
                    MaxNormalAngleDegrees = quality.ReuseMaxNormalAngleDegrees,
                    MaxDepthDeltaMeters = quality.ReuseMaxDepthDeltaMeters,
                    MaxRoughnessBucketDelta = quality.ReuseMaxRoughnessBucketDelta,
                },
                RvcEdgeRejectionPolicy.Conservative,
                RequireAbHarnessBeforeStereoDefault: true,
                KeepSharpSpecularPerView: true),
            RvcTemporalCachePolicy.Default,
            RvcFoveatedResolveContract.Default with
            {
                FovealAntiAliasingPath = quality.FovealAntiAliasingPath,
            },
            RvcOpenXrVisibilityMaskPlan.Resolve(
                requested: settings.PipelineMode != ERvcPipelineMode.Off,
                extensionEnabled: capabilities.OpenXrVisibilityMaskSupported),
            RvcVisibilitySourcePathPlan.Resolve(
                resolution,
                staticMeshPathAvailable: capabilities.StaticMeshVisibilitySourceSupported || capabilities.VisibilityTargetsAvailable,
                skinnedComputePathAvailable: capabilities.SkinnedComputeVisibilitySourceSupported || capabilities.VisibilityTargetsAvailable,
                zeroReadbackMaterialRowsAvailable: capabilities.ZeroReadbackIndirectVisibilitySourceSupported || resolution.DescriptorBackend != ERvcDescriptorBackend.None,
                meshletPathAvailable: capabilities.MeshletVisibilitySourceSupported || capabilities.VulkanMeshShaderSupported),
            RvcGpuPassExecutionPlan.FromPlan(
                resolution,
                visibilityTargetsImplemented: capabilities.VisibilityTargetsAvailable,
                materialCacheImplemented: capabilities.VisibilityTargetsAvailable && resolution.DescriptorBackend != ERvcDescriptorBackend.None,
                sharedLightingImplemented: capabilities.VisibilityTargetsAvailable && (settings.PeripheralLightAggregationEnabled || resolution.IsRvcActive),
                temporalResolveImplemented: capabilities.VisibilityTargetsAvailable && (settings.TemporalReuseEnabled || resolution.IsRvcActive),
                diagnosticOverlayImplemented: settings.DiagnosticOverlayEnabled || resolution.IsRvcActive),
            RvcVulkanProductionPlan.Resolve(
                resolution,
                capabilities,
                dynamicRenderingAvailable: capabilities.VulkanDynamicRenderingSupported,
                synchronization2Available: capabilities.VulkanSynchronization2Supported,
                meshShaderAvailable: capabilities.VulkanMeshShaderSupported,
                timelineSemaphoreAvailable: capabilities.VulkanTimelineSemaphoreSupported),
            RvcExperimentalExtensionPlan.Resolve(
                capabilities,
                meshShaderAvailable: capabilities.VulkanMeshShaderSupported,
                allowPrototypeExtensions: false));

    public static RvcPipelinePlan Build(
        in RvcRenderingSettings settings,
        in RvcPipelineResolution resolution,
        in RvcCapabilityMatrix capabilities)
        => Build(settings, RvcQualitySettings.Defaults, resolution, capabilities);
}
