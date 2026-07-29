using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal struct VulkanImGuiTextureRegistration
{
    internal nint Id;
    internal DescriptorSet DescriptorSet;
    internal ulong ImageViewHandle;
    internal ulong SamplerHandle;
    internal ImageLayout ImageLayout;
    internal ulong DescriptorGeneration;
}
