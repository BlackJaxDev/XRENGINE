using System.Numerics;

namespace XREngine;

public enum ERvcPipelineMode
{
    Off,
    ForwardPlusOracle,
    VisibilityOnlyDebug,
    MaterialCache,
    SharedLighting,
    Full,
}

public enum ERvcFallbackReason
{
    None,
    DisabledBySettings,
    MissingForwardPlusOracle,
    MissingFrameGraph,
    MissingVisibilityTargets,
    MissingDescriptorBackend,
    MissingFoveationRateBackend,
    MissingQuadViewRuntime,
    MissingVisibilityMask,
    MissingMultiview,
    MissingDepthLayerSupport,
    UnsupportedBackend,
    UnsupportedOpenGlProductionPath,
    UnsupportedMaterialClass,
    PayloadOverflow,
    ShadeletMapOverflow,
    DeduplicationOverflow,
    MemoryBudgetExceeded,
    ValidationHarnessFailed,
    StereoReuseDisabledUntilValidated,
    TemporalCacheInvalidated,
    HardwareValidationUnavailable,
    SynchronousCounterReadbackForbidden,
    MissingVisibilitySourcePath,
    MissingVulkanProductionFeature,
}

public enum ERvcDescriptorBackend
{
    None,
    DescriptorHeap,
    DescriptorIndexing,
}

public enum ERvcFoveationRateBackend
{
    None,
    VulkanFragmentShadingRate,
    VulkanFragmentDensityMap,
    OpenXrRuntimeFoveation,
    OpenXrQuadViews,
}

public enum ERvcMaterialClass
{
    Unsupported,
    UnlitOpaque,
    OpaquePbr,
    GeneratedMaterialTableOpaque,
    CheapDeterministicAlphaTest,
    TransparentForwardPlusFallback,
}

public enum ERvcFoveationRegion
{
    Foveal,
    GuardBand,
    MidField,
    Periphery,
}

public enum ERvcShadeletDensity
{
    Rate1x1 = 1,
    Rate2x2 = 2,
    Rate4x4 = 4,
    Rate8x8 = 8,
}

public enum ERvcLightGridSpace
{
    WorldAlignedCameraRelative,
    OrientationSnappedHeadSpace,
}

public enum ERvcValidationScene
{
    DesktopMono,
    Stereo,
    OpaqueDense,
    AvatarMaterialDiverse,
    TransparencyFallback,
    QuadView,
}

public enum ERvcDebugViewMode
{
    Disabled,
    ViewSet,
    HiddenAreaMask,
    Depth,
    LinearDepth,
    VisibilityId,
    InstanceId,
    DrawOrMeshletId,
    PrimitiveId,
    MaterialId,
    TransformId,
    ReconstructionError,
    ShadeletDensity,
    PixelToShadelet,
    ShadeletKeyHash,
    MaterialBinOccupancy,
    ReuseSource,
    ReuseRejectionReason,
    HeadSpaceClusterId,
    ClusterOccupancy,
    ExactLightCount,
    AggregateContribution,
    ReservoirWeight,
    TemporalCacheAge,
    TemporalCacheConfidence,
    FinalResolve,
    TransparentForwardPlusOverlay,
    WideInsetComposition,
    PerformanceCounters,
}

[Flags]
public enum ERvcFrameGraphUsage
{
    None = 0,
    DepthAttachment = 1 << 0,
    ColorAttachment = 1 << 1,
    StorageImage = 1 << 2,
    SampledTexture = 1 << 3,
    StorageBuffer = 1 << 4,
    IndirectBuffer = 1 << 5,
    TransferSource = 1 << 6,
    TransferDestination = 1 << 7,
    DelayedReadback = 1 << 8,
}

public enum ERvcFrameGraphResourceScope
{
    PerView,
    SharedViewSet,
    MirrorOrDebug,
}

public enum ERvcFrameGraphResourceLifetime
{
    Transient,
    Frame,
    Persistent,
}

public readonly record struct RvcRenderingSettings(
    ERvcPipelineMode PipelineMode,
    bool QuadViewEnabled,
    bool StereoReuseEnabled,
    bool InsetWideReuseEnabled,
    bool TemporalReuseEnabled,
    bool PeripheralLightAggregationEnabled,
    bool DiagnosticOverlayEnabled,
    ERvcDebugViewMode DebugViewMode,
    ERvcLightGridSpace LightGridSpace)
{
    public static RvcRenderingSettings Defaults => new(
        ERvcPipelineMode.Off,
        QuadViewEnabled: false,
        StereoReuseEnabled: false,
        InsetWideReuseEnabled: true,
        TemporalReuseEnabled: false,
        PeripheralLightAggregationEnabled: false,
        DiagnosticOverlayEnabled: false,
        ERvcDebugViewMode.Disabled,
        ERvcLightGridSpace.WorldAlignedCameraRelative);
}

public readonly record struct RvcCapabilityMatrix(
    bool ForwardPlusOracleAvailable,
    bool FrameGraphAvailable,
    bool VisibilityTargetsAvailable,
    bool VulkanBackend,
    bool OpenGlBackend,
    bool DescriptorHeapSupported,
    bool DescriptorIndexingSupported,
    bool FragmentShadingRateSupported,
    bool FragmentDensityMapSupported,
    bool OpenXrQuadViewsSupported,
    bool OpenXrRuntimeFoveationSupported,
    bool OpenXrDepthLayersSupported,
    bool OpenXrVisibilityMaskSupported,
    bool MultiviewSupported,
    bool StaticMeshVisibilitySourceSupported = false,
    bool SkinnedComputeVisibilitySourceSupported = false,
    bool ZeroReadbackIndirectVisibilitySourceSupported = false,
    bool MeshletVisibilitySourceSupported = false,
    bool VulkanDynamicRenderingSupported = false,
    bool VulkanSynchronization2Supported = false,
    bool VulkanMeshShaderSupported = false,
    bool VulkanTimelineSemaphoreSupported = false)
{
    public ERvcDescriptorBackend ResolveDescriptorBackend()
    {
        if (DescriptorHeapSupported)
            return ERvcDescriptorBackend.DescriptorHeap;
        if (DescriptorIndexingSupported)
            return ERvcDescriptorBackend.DescriptorIndexing;
        return ERvcDescriptorBackend.None;
    }

    public ERvcFoveationRateBackend ResolveFoveationRateBackend()
    {
        if (FragmentShadingRateSupported)
            return ERvcFoveationRateBackend.VulkanFragmentShadingRate;
        if (FragmentDensityMapSupported)
            return ERvcFoveationRateBackend.VulkanFragmentDensityMap;
        if (OpenXrQuadViewsSupported)
            return ERvcFoveationRateBackend.OpenXrQuadViews;
        if (OpenXrRuntimeFoveationSupported)
            return ERvcFoveationRateBackend.OpenXrRuntimeFoveation;
        return ERvcFoveationRateBackend.None;
    }

    public static RvcCapabilityMatrix ForwardPlusOnly(bool vulkanBackend = false, bool openGlBackend = false)
        => new(
            ForwardPlusOracleAvailable: true,
            FrameGraphAvailable: true,
            VisibilityTargetsAvailable: false,
            VulkanBackend: vulkanBackend,
            OpenGlBackend: openGlBackend,
            DescriptorHeapSupported: false,
            DescriptorIndexingSupported: vulkanBackend,
            FragmentShadingRateSupported: false,
            FragmentDensityMapSupported: false,
            OpenXrQuadViewsSupported: false,
            OpenXrRuntimeFoveationSupported: false,
            OpenXrDepthLayersSupported: false,
            OpenXrVisibilityMaskSupported: false,
            MultiviewSupported: false);
}

public readonly record struct RvcPipelineResolution(
    ERvcPipelineMode RequestedMode,
    ERvcPipelineMode EffectiveMode,
    bool IsRvcActive,
    ERvcDescriptorBackend DescriptorBackend,
    ERvcFoveationRateBackend FoveationRateBackend,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public bool UsesForwardPlusFallback => !IsRvcActive && FallbackReason != ERvcFallbackReason.None;
}

public static class RvcPipelineResolver
{
    public static RvcPipelineResolution Resolve(
        in RvcRenderingSettings settings,
        in RvcCapabilityMatrix capabilities)
    {
        if (settings.PipelineMode == ERvcPipelineMode.Off)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.DisabledBySettings, "RVC is disabled by settings.");

        if (!capabilities.ForwardPlusOracleAvailable)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingForwardPlusOracle, "Forward+ oracle path is unavailable.");

        ERvcDescriptorBackend descriptorBackend = capabilities.ResolveDescriptorBackend();
        ERvcFoveationRateBackend foveationBackend = capabilities.ResolveFoveationRateBackend();

        if (settings.PipelineMode == ERvcPipelineMode.ForwardPlusOracle)
        {
            return new(
                settings.PipelineMode,
                ERvcPipelineMode.ForwardPlusOracle,
                IsRvcActive: false,
                descriptorBackend,
                foveationBackend,
                ERvcFallbackReason.None,
                "Forward+ oracle mode is active; RVC cache passes are intentionally bypassed.");
        }

        if (!capabilities.FrameGraphAvailable)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingFrameGraph, "RVC requires explicit frame-graph resources.");
        if (!capabilities.VisibilityTargetsAvailable)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingVisibilityTargets, "RVC visibility targets are unavailable.");
        if (settings.PipelineMode == ERvcPipelineMode.Full && capabilities.OpenGlBackend)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.UnsupportedOpenGlProductionPath, "Full RVC is Vulkan-only; OpenGL may host correctness slices only.");
        if (settings.PipelineMode >= ERvcPipelineMode.MaterialCache && descriptorBackend == ERvcDescriptorBackend.None)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingDescriptorBackend, "RVC material-cache modes require descriptor heap or descriptor indexing material/resource rows.");

        if (settings.QuadViewEnabled && !capabilities.OpenXrQuadViewsSupported)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingQuadViewRuntime, "Quad-view RVC was requested but the OpenXR quad-view runtime path is unavailable.");
        if (settings.PipelineMode >= ERvcPipelineMode.MaterialCache && foveationBackend == ERvcFoveationRateBackend.None)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingFoveationRateBackend, "RVC material-cache modes require a fragment-shading-rate, fragment-density, or OpenXR foveation backend.");

        return new(
            settings.PipelineMode,
            settings.PipelineMode,
            IsRvcActive: true,
            descriptorBackend,
            foveationBackend,
            ERvcFallbackReason.None,
            "RVC mode is supported by the declared capability matrix.");
    }

    private static RvcPipelineResolution Fallback(
        ERvcPipelineMode requestedMode,
        ERvcFallbackReason reason,
        string diagnostic)
        => new(
            requestedMode,
            ERvcPipelineMode.ForwardPlusOracle,
            IsRvcActive: false,
            ERvcDescriptorBackend.None,
            ERvcFoveationRateBackend.None,
            reason,
            diagnostic);
}

public readonly record struct RvcQualityTolerance(
    ERvcFoveationRegion Region,
    float MaxPerPixelError,
    float MinSsim,
    float MaxFlipError);

public readonly record struct RvcQualityToleranceSet(
    RvcQualityTolerance Foveal,
    RvcQualityTolerance GuardBand,
    RvcQualityTolerance MidField,
    RvcQualityTolerance Periphery)
{
    public static RvcQualityToleranceSet Default => new(
        new(ERvcFoveationRegion.Foveal, 1.0f / 255.0f, 0.995f, 0.010f),
        new(ERvcFoveationRegion.GuardBand, 2.0f / 255.0f, 0.990f, 0.015f),
        new(ERvcFoveationRegion.MidField, 4.0f / 255.0f, 0.975f, 0.030f),
        new(ERvcFoveationRegion.Periphery, 8.0f / 255.0f, 0.940f, 0.060f));

    public RvcQualityTolerance ForRegion(ERvcFoveationRegion region)
        => region switch
        {
            ERvcFoveationRegion.Foveal => Foveal,
            ERvcFoveationRegion.GuardBand => GuardBand,
            ERvcFoveationRegion.MidField => MidField,
            _ => Periphery,
        };
}

public readonly record struct RvcValidationCaptureContract(
    ERvcValidationScene Scene,
    string CameraName,
    Vector2 GazeUv,
    int WarmCacheFrameCount,
    int CaptureFrameIndex,
    bool FixedAnimationTime,
    bool IdenticalSceneState)
{
    public static RvcValidationCaptureContract CreateDefault(ERvcValidationScene scene)
        => new(
            scene,
            "RVC.FixedValidationCamera",
            new Vector2(0.5f, 0.5f),
            WarmCacheFrameCount: 8,
            CaptureFrameIndex: 16,
            FixedAnimationTime: true,
            IdenticalSceneState: true);
}

public readonly record struct RvcAbHarnessContract(
    ERvcValidationScene Scene,
    RvcQualityToleranceSet Tolerances,
    bool ComparePerRegion,
    bool RequireSideBySideImages,
    bool RequireHumanReviewBeforeDefaultStereoReuse)
{
    public static RvcAbHarnessContract Default(ERvcValidationScene scene)
        => new(
            scene,
            RvcQualityToleranceSet.Default,
            ComparePerRegion: true,
            RequireSideBySideImages: true,
            RequireHumanReviewBeforeDefaultStereoReuse: true);
}

public readonly record struct RvcFrameGraphResourceDescriptor(
    string Name,
    ERvcFrameGraphResourceScope Scope,
    ERvcFrameGraphResourceLifetime Lifetime,
    ERvcFrameGraphUsage Usage,
    string Format,
    string DependsOn)
{
    public bool IsPerView => Scope == ERvcFrameGraphResourceScope.PerView;
}

public static class RvcFrameGraphContract
{
    private static readonly RvcFrameGraphResourceDescriptor[] DefaultResourceDescriptors =
    [
        new(PerViewDepth, ERvcFrameGraphResourceScope.PerView, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.DepthAttachment | ERvcFrameGraphUsage.SampledTexture, "D32F or D24S8", string.Empty),
        new(PerViewVisibility, ERvcFrameGraphResourceScope.PerView, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.ColorAttachment | ERvcFrameGraphUsage.StorageImage | ERvcFrameGraphUsage.SampledTexture, "RG32_UINT", PerViewDepth),
        new(PerViewVelocity, ERvcFrameGraphResourceScope.PerView, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.ColorAttachment | ERvcFrameGraphUsage.SampledTexture, "RG16F", PerViewVisibility),
        new(PerViewHzbDepth, ERvcFrameGraphResourceScope.PerView, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageImage | ERvcFrameGraphUsage.SampledTexture, "R32F mip chain", PerViewDepth),
        new(PerViewReconstructionError, ERvcFrameGraphResourceScope.PerView, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageImage | ERvcFrameGraphUsage.SampledTexture, "RGBA16F", PerViewVisibility),
        new(PerViewPixelToShadelet, ERvcFrameGraphResourceScope.PerView, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageImage | ERvcFrameGraphUsage.SampledTexture, "R32_UINT tile-base + R16 local index", PerViewVisibility),
        new(SharedVisibilitySourceRecords, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageBuffer, "RvcVisibilitySourceRecord rows", PerViewVisibility),
        new(SharedMaterialResourceRows, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Persistent, ERvcFrameGraphUsage.StorageBuffer, "renderer-owned material/resource table rows", SharedVisibilitySourceRecords),
        new(SharedOpenXrVisibilityMaskVertices, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Persistent, ERvcFrameGraphUsage.StorageBuffer, "XR_KHR_visibility_mask Vector2 vertices", string.Empty),
        new(SharedOpenXrVisibilityMaskIndices, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Persistent, ERvcFrameGraphUsage.StorageBuffer, "XR_KHR_visibility_mask uint indices", SharedOpenXrVisibilityMaskVertices),
        new(SharedIndirectArguments, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.IndirectBuffer | ERvcFrameGraphUsage.StorageBuffer, "GPU-written indirect arguments and counts", SharedVisibilitySourceRecords),
        new(SharedMaterialShadelets, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageBuffer, "structured shadelet rows", PerViewPixelToShadelet),
        new(SharedHeadSpaceLightClusters, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageBuffer, "cluster records + light index ranges", SharedMaterialShadelets),
        new(SharedLighting, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageBuffer, "material shadelet lighting rows", SharedHeadSpaceLightClusters),
        new(SharedLightReservoirs, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageBuffer, "peripheral reservoir candidates", SharedHeadSpaceLightClusters),
        new(SharedTemporalCache, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Persistent, ERvcFrameGraphUsage.StorageBuffer, "world-space temporal shadelet hash grid", SharedLighting),
        new(SharedCounters, ERvcFrameGraphResourceScope.SharedViewSet, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.StorageBuffer | ERvcFrameGraphUsage.DelayedReadback, "delayed RVC GPU counters", SharedTemporalCache),
        new(TransparencyTarget, ERvcFrameGraphResourceScope.PerView, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.ColorAttachment | ERvcFrameGraphUsage.SampledTexture, "RGBA16F", SharedLighting),
        new(FinalResolve, ERvcFrameGraphResourceScope.PerView, ERvcFrameGraphResourceLifetime.Frame, ERvcFrameGraphUsage.ColorAttachment | ERvcFrameGraphUsage.TransferSource, "RGBA16F/RGBA8", TransparencyTarget),
        new(MirrorDebug, ERvcFrameGraphResourceScope.MirrorOrDebug, ERvcFrameGraphResourceLifetime.Transient, ERvcFrameGraphUsage.ColorAttachment | ERvcFrameGraphUsage.TransferSource, "RGBA8", FinalResolve),
    ];

    public const string PerViewDepth = "RVC/View{0}.{1}/Depth";
    public const string PerViewVisibility = "RVC/View{0}.{1}/VisibilityID";
    public const string PerViewVelocity = "RVC/View{0}.{1}/Velocity";
    public const string PerViewHzbDepth = "RVC/View{0}.{1}/HZBDepth";
    public const string PerViewReconstructionError = "RVC/View{0}.{1}/ReconstructionError";
    public const string PerViewPixelToShadelet = "RVC/View{0}.{1}/PixelToShadelet";
    public const string PerViewDepthArray = "RVC/Views/Depth";
    public const string PerViewVisibilityArray = "RVC/Views/VisibilityID";
    public const string PerViewVelocityArray = "RVC/Views/Velocity";
    public const string PerViewHzbDepthArray = "RVC/Views/HZBDepth";
    public const string PerViewReconstructionErrorArray = "RVC/Views/ReconstructionError";
    public const string PerViewPixelToShadeletArray = "RVC/Views/PixelToShadelet";
    public const string SharedVisibilitySourceRecords = "RVC/Shared/VisibilitySourceRecords";
    public const string SharedMaterialResourceRows = "RVC/Shared/MaterialResourceRows";
    public const string SharedOpenXrVisibilityMaskVertices = "RVC/Shared/OpenXRVisibilityMaskVertices";
    public const string SharedOpenXrVisibilityMaskIndices = "RVC/Shared/OpenXRVisibilityMaskIndices";
    public const string SharedIndirectArguments = "RVC/Shared/IndirectArguments";
    public const string SharedMaterialShadelets = "RVC/Shared/MaterialShadelets";
    public const string SharedLighting = "RVC/Shared/SharedLighting";
    public const string SharedHeadSpaceLightClusters = "RVC/Shared/HeadSpaceLightClusters";
    public const string SharedLightReservoirs = "RVC/Shared/LightReservoirs";
    public const string SharedTemporalCache = "RVC/Shared/TemporalCache";
    public const string SharedCounters = "RVC/Shared/Counters";
    public const string TransparencyTarget = "RVC/View{0}.{1}/TransparentForwardPlus";
    public const string FinalResolve = "RVC/View{0}.{1}/FinalResolve";
    public const string TransparencyTargetArray = "RVC/Views/TransparentForwardPlus";
    public const string FinalResolveArray = "RVC/Views/FinalResolve";
    public const string MirrorDebug = "RVC/Mirror/DebugOutput";
    public const string VisibilityFrameBuffer = "RVC/FBO/Visibility";
    public const string TransparencyFrameBuffer = "RVC/FBO/Transparency";
    public const string ResolveFrameBuffer = "RVC/FBO/Resolve";
    public const string DebugFrameBuffer = "RVC/FBO/Debug";

    public static ReadOnlySpan<RvcFrameGraphResourceDescriptor> DefaultResources
        => DefaultResourceDescriptors;

    public static string MakePerViewName(string pattern, int viewIndex, EVrOutputViewKind kind)
        => string.Format(pattern, viewIndex, kind);
}

public readonly record struct RvcVisibilityPayload(ulong Packed)
{
    private const int FieldBits = 20;
    private const uint FieldMask = (1u << FieldBits) - 1u;

    public uint InstanceId => (uint)(Packed & FieldMask);
    public uint DrawOrMeshletId => (uint)((Packed >> FieldBits) & FieldMask);
    public uint PrimitiveId => (uint)((Packed >> (FieldBits * 2)) & FieldMask);
    public uint Flags => (uint)(Packed >> 60);
    public bool HasOverflow => (Flags & 0x1u) != 0u;

    public static bool TryPack(
        uint instanceId,
        uint drawOrMeshletId,
        uint primitiveId,
        uint flags,
        out RvcVisibilityPayload payload)
    {
        bool overflow =
            instanceId > FieldMask ||
            drawOrMeshletId > FieldMask ||
            primitiveId > FieldMask ||
            flags > 0xFu;

        uint packedFlags = (flags & 0xFu) | (overflow ? 0x1u : 0u);
        ulong packed =
            ((ulong)(instanceId & FieldMask)) |
            ((ulong)(drawOrMeshletId & FieldMask) << FieldBits) |
            ((ulong)(primitiveId & FieldMask) << (FieldBits * 2)) |
            ((ulong)packedFlags << 60);

        payload = new RvcVisibilityPayload(packed);
        return !overflow;
    }
}

public readonly record struct RvcVisibilitySourceRecord(
    uint InstanceId,
    uint DrawOrMeshletId,
    uint PrimitiveId,
    uint MaterialRowId,
    uint TransformId,
    uint EditorSelectionId,
    uint DeformationVersion,
    uint MaterialResourceGeneration);

public readonly record struct RvcSurfaceKey(
    ushort QuantizedU,
    ushort QuantizedV,
    ushort RoughnessBucket,
    byte LodBucket,
    ERvcFoveationRegion Region);

public readonly record struct RvcShadeletKey(ulong A, ulong B)
{
    public static RvcShadeletKey Create(
        in RvcVisibilityPayload visibility,
        ERvcMaterialClass materialClass,
        in RvcSurfaceKey surface,
        uint materialRowId,
        uint deformationVersion)
    {
        ulong b =
            ((ulong)materialRowId & 0xFFFFFu) |
            (((ulong)surface.QuantizedU & 0x3FFFu) << 20) |
            (((ulong)surface.QuantizedV & 0x3FFFu) << 34) |
            (((ulong)surface.LodBucket & 0xFFu) << 48) |
            (((ulong)surface.RoughnessBucket & 0xFFu) << 56);

        ulong classRegionDeform =
            (((ulong)materialClass & 0xFu) << 4) |
            (((ulong)surface.Region & 0xFu)) |
            (((ulong)deformationVersion & 0xFFFFu) << 8);

        return new RvcShadeletKey(visibility.Packed ^ (classRegionDeform << 40), b);
    }
}

public readonly record struct RvcShadeletRecord(
    RvcShadeletKey Key,
    uint MaterialRowId,
    uint MaterialResourceGeneration,
    ERvcMaterialClass MaterialClass,
    ERvcShadeletDensity Density,
    bool AllowsStereoReuse,
    bool RequiresPerViewSpecular,
    byte RoughnessBucket);

public readonly record struct RvcShadeletReuseCandidate(
    RvcShadeletKey Key,
    uint MaterialResourceGeneration,
    Vector3 Normal,
    float DepthMeters,
    byte RoughnessBucket,
    uint DeformationVersion,
    byte LodBucket,
    bool Disoccluded,
    bool ViewDependentMaterial);

public static class RvcShadeletReuseValidator
{
    public static bool CanReuse(
        in RvcShadeletReuseCandidate source,
        in RvcShadeletReuseCandidate target,
        bool stereoReuse,
        float maxNormalAngleDegrees,
        float maxDepthDeltaMeters,
        byte maxRoughnessBucketDelta,
        out ERvcFallbackReason rejectionReason)
    {
        if (!stereoReuse)
        {
            rejectionReason = ERvcFallbackReason.StereoReuseDisabledUntilValidated;
            return false;
        }

        if (source.Key != target.Key ||
            source.MaterialResourceGeneration != target.MaterialResourceGeneration ||
            source.DeformationVersion != target.DeformationVersion ||
            source.LodBucket != target.LodBucket)
        {
            rejectionReason = ERvcFallbackReason.UnsupportedMaterialClass;
            return false;
        }

        if (source.Disoccluded || target.Disoccluded || source.ViewDependentMaterial || target.ViewDependentMaterial)
        {
            rejectionReason = ERvcFallbackReason.ValidationHarnessFailed;
            return false;
        }

        float normalDot = Vector3.Dot(Vector3.Normalize(source.Normal), Vector3.Normalize(target.Normal));
        normalDot = Math.Clamp(normalDot, -1.0f, 1.0f);
        float angle = MathF.Acos(normalDot) * 180.0f / MathF.PI;
        if (angle > maxNormalAngleDegrees || MathF.Abs(source.DepthMeters - target.DepthMeters) > maxDepthDeltaMeters)
        {
            rejectionReason = ERvcFallbackReason.ValidationHarnessFailed;
            return false;
        }

        int roughnessDelta = Math.Abs(source.RoughnessBucket - target.RoughnessBucket);
        if (roughnessDelta > maxRoughnessBucketDelta)
        {
            rejectionReason = ERvcFallbackReason.ValidationHarnessFailed;
            return false;
        }

        rejectionReason = ERvcFallbackReason.None;
        return true;
    }
}

public readonly record struct RvcHeadSpaceClusterKey(int X, int Y, int Z)
{
    public static RvcHeadSpaceClusterKey FromWorldPosition(
        Vector3 worldPosition,
        Vector3 cameraRelativeOrigin,
        float cellSizeMeters)
    {
        float inv = 1.0f / MathF.Max(cellSizeMeters, 0.0001f);
        Vector3 relative = worldPosition - cameraRelativeOrigin;
        return new(
            (int)MathF.Floor(relative.X * inv),
            (int)MathF.Floor(relative.Y * inv),
            (int)MathF.Floor(relative.Z * inv));
    }
}

public readonly record struct RvcLightReservoir(
    uint SelectedLightId,
    float SelectedWeight,
    float WeightSum,
    uint CandidateCount)
{
    public static RvcLightReservoir Empty => default;

    public RvcLightReservoir Add(uint lightId, float weight, float random01)
    {
        float clampedWeight = MathF.Max(0.0f, weight);
        float newSum = WeightSum + clampedWeight;
        uint newCount = CandidateCount + 1u;
        bool select = newSum > 0.0f && random01 <= clampedWeight / newSum;
        return new(
            select ? lightId : SelectedLightId,
            select ? clampedWeight : SelectedWeight,
            newSum,
            newCount);
    }

    public static RvcLightReservoir Combine(
        in RvcLightReservoir a,
        in RvcLightReservoir b,
        float random01)
    {
        if (a.CandidateCount == 0u)
            return b;
        if (b.CandidateCount == 0u)
            return a;

        float combinedWeight = a.WeightSum + b.WeightSum;
        bool selectB = combinedWeight > 0.0f && random01 <= b.WeightSum / combinedWeight;
        return new(
            selectB ? b.SelectedLightId : a.SelectedLightId,
            selectB ? b.SelectedWeight : a.SelectedWeight,
            combinedWeight,
            a.CandidateCount + b.CandidateCount);
    }
}

public readonly record struct RvcTemporalHashGridKey(int X, int Y, int Z, int NormalOctX, int NormalOctY, byte RoughnessBucket)
{
    public static RvcTemporalHashGridKey FromSurface(
        Vector3 worldPosition,
        Vector3 normal,
        float cellSizeMeters,
        byte roughnessBucket)
    {
        float inv = 1.0f / MathF.Max(cellSizeMeters, 0.0001f);
        Vector3 n = Vector3.Normalize(normal);
        float denom = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        Vector2 oct = denom > 0.0f ? new Vector2(n.X, n.Y) / denom : Vector2.Zero;
        if (n.Z < 0.0f)
            oct = new Vector2(
                (1.0f - MathF.Abs(oct.Y)) * MathF.Sign(oct.X),
                (1.0f - MathF.Abs(oct.X)) * MathF.Sign(oct.Y));

        return new(
            (int)MathF.Floor(worldPosition.X * inv),
            (int)MathF.Floor(worldPosition.Y * inv),
            (int)MathF.Floor(worldPosition.Z * inv),
            (int)MathF.Round(oct.X * 127.0f),
            (int)MathF.Round(oct.Y * 127.0f),
            roughnessBucket);
    }
}
