namespace XREngine.Rendering.Vulkan;

/// <summary>Exact five-buffer and private-atlas authority for one prepared glyph run.</summary>
internal readonly record struct VulkanStaticUiTextNativeAuthority(
    VulkanStaticUiTextBufferAuthority GlyphTransforms,
    VulkanStaticUiTextBufferAuthority GlyphTexCoords,
    VulkanStaticUiTextBufferAuthority TextInstances,
    VulkanStaticUiTextBufferAuthority GlyphIndices,
    VulkanStaticUiTextBufferAuthority DrawIndices,
    VkImageDescriptorSnapshot AtlasSnapshot,
    VulkanRecordedDescriptorResourceIdentity AtlasResource,
    VulkanDescriptorPayload AtlasPayload,
    uint AtlasSet,
    uint AtlasBinding)
{
    internal bool IsComplete => GlyphTransforms.IsComplete &&
        GlyphTexCoords.IsComplete && TextInstances.IsComplete &&
        GlyphIndices.IsComplete && DrawIndices.IsComplete &&
        AtlasSnapshot.IsReady && AtlasResource.IsComplete &&
        AtlasPayload.DescriptorType ==
            Silk.NET.Vulkan.DescriptorType.CombinedImageSampler &&
        AtlasPayload.PrimaryHandle != 0 && AtlasPayload.SecondaryHandle != 0 &&
        AtlasSet != uint.MaxValue && AtlasBinding != uint.MaxValue;
}
