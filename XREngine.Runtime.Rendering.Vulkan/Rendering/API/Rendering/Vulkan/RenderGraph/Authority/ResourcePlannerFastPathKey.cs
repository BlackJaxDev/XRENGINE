using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct ResourcePlannerFastPathKey(
    RenderResourceRegistry? Registry,
    int RegistryDescriptorRevision,
    IReadOnlyCollection<RenderPassMetadata>? ActivePassMetadata,
    int ActivePassMetadataRevision,
    int ActivePassSetSignature,
    int ActiveResourceSetSignature,
    int OutputFrameBufferIdentity,
    int OutputTargetIdentity,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership,
    bool SupportsTransformFeedback)
{
    public bool Matches(in ResourcePlannerFastPathKey other)
        => ReferenceEquals(Registry, other.Registry) &&
           RegistryDescriptorRevision == other.RegistryDescriptorRevision &&
           ReferenceEquals(ActivePassMetadata, other.ActivePassMetadata) &&
           ActivePassMetadataRevision == other.ActivePassMetadataRevision &&
           ActivePassSetSignature == other.ActivePassSetSignature &&
           ActiveResourceSetSignature == other.ActiveResourceSetSignature &&
           OutputFrameBufferIdentity == other.OutputFrameBufferIdentity &&
           OutputTargetIdentity == other.OutputTargetIdentity &&
           DisplayWidth == other.DisplayWidth &&
           DisplayHeight == other.DisplayHeight &&
           InternalWidth == other.InternalWidth &&
           InternalHeight == other.InternalHeight &&
           QueueOwnership.Equals(other.QueueOwnership) &&
           SupportsTransformFeedback == other.SupportsTransformFeedback;
}
