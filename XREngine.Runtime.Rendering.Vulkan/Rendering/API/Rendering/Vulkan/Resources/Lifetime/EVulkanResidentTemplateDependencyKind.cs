using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Native object categories permitted in a resident-template dependency lease.
/// The explicit mapping prevents a raw handle from being pinned as the wrong
/// Vulkan object type.
/// </summary>
internal enum EVulkanResidentTemplateDependencyKind : byte
{
    Pipeline,
    PipelineLayout,
    DescriptorSetLayout,
    Buffer,
    BufferView,
    RenderPass,
}
