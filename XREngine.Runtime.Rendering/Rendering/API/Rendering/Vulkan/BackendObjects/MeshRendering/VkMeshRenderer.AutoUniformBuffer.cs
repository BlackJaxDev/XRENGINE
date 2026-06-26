using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        private readonly struct AutoUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory, uint size, void* mappedPtr)
        {
            public Silk.NET.Vulkan.Buffer Buffer { get; } = buffer;
            public DeviceMemory Memory { get; } = memory;
            public uint Size { get; } = size;
            public void* MappedPtr { get; } = mappedPtr;
        }
    }
}

// Remaining VkMeshRenderer implementation lives in partial files:
// - VkMeshRenderer.Buffers.cs
// - VkMeshRenderer.Pipeline.cs
// - VkMeshRenderer.Drawing.cs
// - VkMeshRenderer.Descriptors.cs
// - VkMeshRenderer.Uniforms.cs
// - VkMeshRenderer.Cleanup.cs
