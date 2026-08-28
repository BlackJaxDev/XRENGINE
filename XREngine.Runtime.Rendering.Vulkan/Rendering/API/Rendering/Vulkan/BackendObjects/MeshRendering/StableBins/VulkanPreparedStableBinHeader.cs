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
    VulkanResidentDrawTemplateNativeState NativeState = default)
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
}
