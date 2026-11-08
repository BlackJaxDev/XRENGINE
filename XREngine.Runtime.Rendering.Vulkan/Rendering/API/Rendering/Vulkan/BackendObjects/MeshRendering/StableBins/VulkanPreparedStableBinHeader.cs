namespace XREngine.Rendering.Vulkan;

/// <summary>One contiguous exact-key range in a frozen current-frame bin stream.</summary>
internal readonly record struct VulkanPreparedStableBinHeader(
    VulkanRenderBinKey Key,
    int RecordOffset,
    int RecordCount,
    VulkanBinResourceManifest ResourceManifest,
    VulkanSealedBinSubmissionPlan? SubmissionPlan = null,
    AdvancedIndirectRange IndirectRange = default,
    VulkanVisibilityRasterPipeline RasterPipeline = default,
    VulkanResidentDrawTemplateNativeState NativeState = default,
    VulkanVisibilityRasterPipeline ShadowRasterPipeline = default,
    VulkanVisibilityRasterPipeline DecalRasterPipeline = default,
    VulkanResidentDrawTemplateNativeState DecalNativeState = default)
{
    /// <summary>
    /// A bin is recordable by the advanced visibility raster lane only after
    /// its strategy and exact producer-owned indirect range were frozen as one
    /// transaction. A null plan intentionally leaves the header unusable by
    /// that lane rather than selecting a late fallback.
    /// </summary>
    internal bool HasSealedSubmission => SubmissionPlan is not null;
    internal bool IsRasterReady => HasSealedSubmission && RasterPipeline.IsValid &&
        NativeState.IsValid;
    /// <summary>
    /// The directional shadow lane reuses this bin's geometry with its own
    /// depth-only pipeline for the atlas-page closure.
    /// </summary>
    internal bool IsDirectionalShadowReady => IsRasterReady && ShadowRasterPipeline.IsValid;

    /// <summary>
    /// A mesh surface decal bin. Only the decal buffer lane draws it; the visibility
    /// raster and the shadow lanes skip it.
    /// </summary>
    internal bool IsSurfaceDecal => IndirectRange.Key.Coverage == EAdvancedMaterialCoverageMode.Decal;

    /// <summary>The decal buffer lane can draw this bin with its blended pipeline.</summary>
    internal bool IsDecalBufferReady => HasSealedSubmission && IsSurfaceDecal &&
        DecalRasterPipeline.IsValid && DecalNativeState.IsValid;
}
