using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
internal interface IVkTexelBufferDescriptorSource
{
    BufferView DescriptorBufferView { get; }
    Format DescriptorBufferFormat { get; }
}
