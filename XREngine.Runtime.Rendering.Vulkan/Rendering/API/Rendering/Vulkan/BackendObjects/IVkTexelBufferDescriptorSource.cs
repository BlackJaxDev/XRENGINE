using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal interface IVkTexelBufferDescriptorSource
    {
        BufferView DescriptorBufferView { get; }
        Format DescriptorBufferFormat { get; }
    }
}
