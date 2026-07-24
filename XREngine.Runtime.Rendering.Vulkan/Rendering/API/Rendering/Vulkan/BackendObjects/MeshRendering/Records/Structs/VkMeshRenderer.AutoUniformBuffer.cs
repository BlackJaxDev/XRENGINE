using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        private readonly struct AutoUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory, uint size, void* mappedPtr, ulong offset = 0, bool ownsBuffer = true)
        {
            public Silk.NET.Vulkan.Buffer Buffer { get; } = buffer;
            public DeviceMemory Memory { get; } = memory;
            public uint Size { get; } = size;
            public void* MappedPtr { get; } = mappedPtr;
            public ulong Offset { get; } = offset;
            public bool OwnsBuffer { get; } = ownsBuffer;
        }
    }
}