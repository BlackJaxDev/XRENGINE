using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>Caches the active-pass metadata filter result for one registry and pass-set input.</summary>
internal readonly record struct ActivePassMetadataFilterCacheEntry(
    IReadOnlyCollection<RenderPassMetadata> SourcePassMetadata,
    RenderResourceRegistry? ResourceRegistry,
    int ResourceRegistryRevision,
    int ActivePassSetSignature,
    int ActiveResourceSetSignature,
    bool ConstrainToActivePassSet,
    IReadOnlyCollection<RenderPassMetadata> Result)
{
    public bool Matches(
        IReadOnlyCollection<RenderPassMetadata> sourcePassMetadata,
        RenderResourceRegistry? resourceRegistry,
        int resourceRegistryRevision,
        int activePassSetSignature,
        int activeResourceSetSignature,
        bool constrainToActivePassSet)
        => ReferenceEquals(SourcePassMetadata, sourcePassMetadata)
            && ReferenceEquals(ResourceRegistry, resourceRegistry)
            && ResourceRegistryRevision == resourceRegistryRevision
            && ActivePassSetSignature == activePassSetSignature
            && ActiveResourceSetSignature == activeResourceSetSignature
            && ConstrainToActivePassSet == constrainToActivePassSet;
}
