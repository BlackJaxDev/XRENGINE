using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private readonly struct AutoUniformBuffer(
        Silk.NET.Vulkan.Buffer buffer,
        DeviceMemory memory,
        uint size,
        ulong offset = 0,
        bool ownsBuffer = true,
        VulkanMappedFrameSlice mappedSlice = default,
        VulkanMappedMemorySlice mappedMemorySlice = default)
    {
        public Silk.NET.Vulkan.Buffer Buffer { get; } = buffer;
        public DeviceMemory Memory { get; } = memory;
        public uint Size { get; } = size;
        public ulong Offset { get; } = offset;
        public bool OwnsBuffer { get; } = ownsBuffer;
        public VulkanMappedFrameSlice MappedSlice { get; } = mappedSlice;
        public VulkanMappedMemorySlice MappedMemorySlice { get; } = mappedMemorySlice;
        public bool UsesMappedFrameArena => MappedSlice.IsValid;
        public bool UsesMappedMemoryLease => MappedMemorySlice.Buffer.Handle != 0;
    }
}
