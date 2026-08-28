namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact immutable identity of the one set-1 visibility family published for
/// a frame-slot generation. Shape equality is insufficient: two outputs can
/// have equal counts while consuming different extractors or native scenes.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityFamilySeal(
    object Owner,
    AdvancedPreparationExtractor Extractor,
    AdvancedPreparationPublication Publication,
    ulong VisibilityContentGeneration,
    AdvancedIndirectPreparationResult Indirect,
    VulkanAdvancedSceneLookupSegments LookupSegments,
    VulkanAdvancedVisibilityGeometrySlices Geometry,
    ulong SceneNativeGeneration,
    uint ViewCount)
{
    internal bool IsValid
        => Owner is not null && Extractor is not null &&
           Publication.PublicationGeneration != 0u &&
           VisibilityContentGeneration != 0u &&
           Publication.VisibilityContentGeneration == VisibilityContentGeneration &&
           SceneNativeGeneration != 0u && ViewCount != 0u && Geometry.IsValid;

    internal bool Matches(in VulkanAdvancedVisibilityFamilySeal other)
        => ReferenceEquals(Owner, other.Owner) &&
           ReferenceEquals(Extractor, other.Extractor) &&
           Publication.Equals(other.Publication) &&
           VisibilityContentGeneration == other.VisibilityContentGeneration &&
           Indirect.Equals(other.Indirect) &&
           LookupSegments.Equals(other.LookupSegments) &&
           Geometry.Equals(other.Geometry) &&
           SceneNativeGeneration == other.SceneNativeGeneration &&
           ViewCount == other.ViewCount;
}
