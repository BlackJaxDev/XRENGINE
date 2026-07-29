using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal struct VulkanImGuiDrawBufferSet
{
    internal Buffer VertexBuffer;
    internal DeviceMemory VertexBufferMemory;
    internal ulong VertexBufferSize;
    internal Buffer IndexBuffer;
    internal DeviceMemory IndexBufferMemory;
    internal ulong IndexBufferSize;
}
