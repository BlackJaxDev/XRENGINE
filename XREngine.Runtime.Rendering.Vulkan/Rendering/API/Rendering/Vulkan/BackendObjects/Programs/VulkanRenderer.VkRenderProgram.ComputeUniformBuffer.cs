using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;


internal partial class VkRenderProgram
{
    private readonly struct ComputeUniformBuffer(
        Silk.NET.Vulkan.Buffer buffer,
        DeviceMemory memory,
        uint size,
        VulkanMappedMemorySlice slice)
    {
        public Silk.NET.Vulkan.Buffer Buffer { get; } = buffer;
        public DeviceMemory Memory { get; } = memory;
        public uint Size { get; } = size;
        public VulkanMappedMemorySlice Slice { get; } = slice;
    }

}
