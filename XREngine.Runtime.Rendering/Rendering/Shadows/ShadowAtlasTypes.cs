using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Shadows;

public enum EShadowProjectionType
{
    DirectionalPrimary = 0,
    DirectionalCascade = 1,
    SpotPrimary = 2,
    PointFace = 3,
}

public enum EShadowAtlasKind
{
    Directional = 0,
    Point = 1,
    Spot = 2,
}

public enum ShadowRequestDomain
{
    Live = 0,
    Bake = 1,
    Probe = 2,
    Capture = 3,
}

public enum ShadowFallbackMode
{
    None = 0,
    Lit = 1,
    ContactOnly = 2,
    StaleTile = 3,
    Disabled = 4,
    Legacy = 5,
}

public enum ShadowCasterFilterMode
{
    Opaque = 0,
    AlphaTested = 1,
    TwoSided = 2,
}

[Flags]
public enum StereoVisibility
{
    None = 0,
    Mono = 1,
    LeftEye = 2,
    RightEye = 4,
    BothEyes = LeftEye | RightEye,
    All = Mono | BothEyes,
}

public enum SkipReason
{
    None = 0,
    DisabledByLight = 1,
    NoCaster = 2,
    NoConsumerCamera = 3,
    BelowMinimumPriority = 4,
    TileBudgetExceeded = 5,
    RenderTimeBudgetExceeded = 6,
    PageBudgetExceeded = 7,
    MemoryBudgetExceeded = 8,
    UnsupportedEncoding = 9,
    UnsupportedFormat = 10,
    EncodingDemoted = 11,
    ResolutionBelowMinimum = 12,
    AllocationFailed = 13,
    QueueOverflow = 14,
    ProbeOptOut = 15,
    BakingUsesLegacyPath = 16,
    StaleTileReused = 17,
    EditorPinnedHardMemoryCap = 18,
    InvalidRequest = 19,
    ResourceCreationFailed = 20,
    NotRelevant = 21,
}

[Flags]
public enum ShadowDirtyReason
{
    None = 0,
    FirstSubmission = 1 << 0,
    ContentChanged = 1 << 1,
    LightOrSettingsChanged = 1 << 2,
    ProjectionOrCameraFitChanged = 1 << 3,
    EncodingChanged = 1 << 4,
    CasterOrMaterialChanged = 1 << 5,
    AllocationMissing = 1 << 6,
    AllocationChanged = 1 << 7,
    NeverRendered = 1 << 8,
    ReuseDisabled = 1 << 9,
    DynamicLight = 1 << 10,
}

public readonly record struct ShadowRequestKey(
    Guid LightId,
    ShadowRequestDomain Domain,
    EShadowProjectionType ProjectionType,
    int FaceOrCascadeIndex,
    EShadowMapEncoding Encoding) : IComparable<ShadowRequestKey>
{
    public int CompareTo(ShadowRequestKey other)
    {
        int result = Domain.CompareTo(other.Domain);
        if (result != 0)
            return result;

        result = ProjectionType.CompareTo(other.ProjectionType);
        if (result != 0)
            return result;

        result = LightId.CompareTo(other.LightId);
        if (result != 0)
            return result;

        result = FaceOrCascadeIndex.CompareTo(other.FaceOrCascadeIndex);
        return result != 0 ? result : Encoding.CompareTo(other.Encoding);
    }
}

public readonly record struct ShadowMapRequest(
    ShadowRequestKey Key,
    LightComponent Light,
    EShadowProjectionType ProjectionType,
    EShadowMapEncoding Encoding,
    ShadowCasterFilterMode CasterMode,
    ShadowFallbackMode Fallback,
    int FaceOrCascadeIndex,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    float NearPlane,
    float FarPlane,
    uint DesiredResolution,
    uint MinimumResolution,
    float Priority,
    ulong ContentHash,
    bool IsDirty,
    ShadowDirtyReason DirtyReason,
    bool CanReusePreviousFrame,
    bool EditorPinned,
    StereoVisibility StereoVis,
    SkipReason ForcedSkipReason = SkipReason.None);

public readonly record struct ShadowAtlasAllocation(
    ShadowRequestKey Key,
    EShadowAtlasKind AtlasKind,
    int AtlasId,
    int PageIndex,
    BoundingRectangle PixelRect,
    BoundingRectangle InnerPixelRect,
    Vector4 UvScaleBias,
    uint Resolution,
    int LodLevel,
    ulong ContentVersion,
    ulong LastRenderedFrame,
    bool IsResident,
    bool IsStaticCacheBacked,
    ShadowFallbackMode ActiveFallback,
    SkipReason SkipReason);

public readonly record struct ShadowAtlasGroupedAllocationMember(
    int CascadeIndex,
    int RecordIndex,
    BoundingRectangle PixelRect,
    BoundingRectangle InnerPixelRect,
    int ViewportScissorIndex,
    Vector4 UvScaleBias);

public readonly record struct ShadowAtlasGroupedDirectionalCascadeAllocation(
    Guid LightId,
    ShadowRequestDomain Domain,
    EShadowMapEncoding Encoding,
    EShadowAtlasKind AtlasKind,
    int AtlasId,
    int PageIndex,
    int CascadeCount,
    ShadowAtlasGroupedAllocationMember[] Members);

public readonly record struct ShadowAtlasGroupedPointFaceAllocation(
    Guid LightId,
    ShadowRequestDomain Domain,
    EShadowMapEncoding Encoding,
    EShadowAtlasKind AtlasKind,
    int AtlasId,
    int PageIndex,
    int FaceCount,
    ShadowAtlasGroupedAllocationMember[] Members);

public readonly record struct ShadowAtlasPageDescriptor(
    EShadowAtlasKind AtlasKind,
    EShadowMapEncoding Encoding,
    int PageIndex,
    uint PageSize,
    EPixelInternalFormat InternalFormat,
    EPixelFormat PixelFormat,
    EPixelType PixelType,
    long EstimatedBytes);

public readonly record struct ShadowAtlasMetrics(
    ulong FrameId,
    ulong Generation,
    int RequestCount,
    int ResidentTileCount,
    int SkippedRequestCount,
    int PageCount,
    long ResidentBytes,
    int TilesScheduledThisFrame,
    int QueueOverflowCount,
    int NotRelevantSkipCount,
    int LargestFreeRect,
    long FreeTexelCount);

public readonly record struct ShadowRequestDiagnostic(
    int RequestCount,
    int ResidentCount,
    uint MaxRequestedResolution,
    uint MaxAllocatedResolution,
    float HighestPriority,
    SkipReason LastSkipReason,
    ShadowDirtyReason LastDirtyReason,
    ShadowFallbackMode ActiveFallback,
    int ShadowRecordIndex,
    int AtlasPageIndex,
    BoundingRectangle AtlasPixelRect,
    BoundingRectangle AtlasInnerPixelRect,
    ulong LastRenderedFrame,
    ulong LastFrameId);

public readonly record struct ShadowAtlasManagerSettings(
    uint PageSize,
    int MaxPages,
    long MaxMemoryBytes,
    int MaxTilesRenderedPerFrame,
    float MaxRenderMilliseconds,
    uint MinTileResolution,
    uint MaxTileResolution,
    int MaxRequestsPerFrame)
{
    public static ShadowAtlasManagerSettings Default => new(
        PageSize: 4096u,
        MaxPages: 1,
        MaxMemoryBytes: 0L,
        MaxTilesRenderedPerFrame: 16,
        MaxRenderMilliseconds: 2.0f,
        MinTileResolution: 128u,
        MaxTileResolution: 4096u,
        MaxRequestsPerFrame: 4096);

    public static ShadowAtlasManagerSettings FromCurrentRuntimeSettings()
        => new(
            PageSize: RuntimeEngine.Rendering.Settings.ShadowAtlasPageSize,
            MaxPages: RuntimeEngine.Rendering.Settings.MaxShadowAtlasPages,
            MaxMemoryBytes: RuntimeEngine.Rendering.Settings.MaxShadowAtlasMemoryBytes,
            MaxTilesRenderedPerFrame: RuntimeEngine.Rendering.Settings.MaxShadowTilesRenderedPerFrame,
            MaxRenderMilliseconds: RuntimeEngine.Rendering.Settings.MaxShadowRenderMilliseconds,
            MinTileResolution: RuntimeEngine.Rendering.Settings.MinShadowAtlasTileResolution,
            MaxTileResolution: RuntimeEngine.Rendering.Settings.MaxShadowAtlasTileResolution,
            MaxRequestsPerFrame: Default.MaxRequestsPerFrame);
}
