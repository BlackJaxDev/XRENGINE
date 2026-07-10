namespace XREngine;

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
