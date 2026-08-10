namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-facing capability projection. Descriptor ownership remains with the
/// generation-local descriptor manager.
/// </summary>
public partial class VulkanRenderer
{
    internal bool SupportsDescriptorIndexing
        => ResourceRuntime.Descriptors.SupportsDescriptorIndexing;
}
