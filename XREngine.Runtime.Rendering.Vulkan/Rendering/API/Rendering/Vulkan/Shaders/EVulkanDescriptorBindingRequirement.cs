namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Declares whether an unresolved descriptor is a hard recording precondition
/// or may intentionally use the renderer's diagnostic placeholder.
/// </summary>
internal enum EVulkanDescriptorBindingRequirement : byte
{
    Required,
    Optional,
}
