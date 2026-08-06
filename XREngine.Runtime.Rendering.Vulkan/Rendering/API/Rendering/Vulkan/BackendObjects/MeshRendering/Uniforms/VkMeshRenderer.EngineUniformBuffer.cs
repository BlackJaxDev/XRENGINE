using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private readonly struct EngineUniformBuffer(
        Silk.NET.Vulkan.Buffer buffer,
        DeviceMemory memory,
        uint size,
        void* mappedPtr,
        ulong offset = 0,
        bool ownsBuffer = true,
        VulkanMappedFrameSlice mappedSlice = default)
    {
        public Silk.NET.Vulkan.Buffer Buffer { get; } = buffer;
        public DeviceMemory Memory { get; } = memory;
        public uint Size { get; } = size;
        public void* MappedPtr { get; } = mappedPtr;
        public ulong Offset { get; } = offset;
        public bool OwnsBuffer { get; } = ownsBuffer;
        public VulkanMappedFrameSlice MappedSlice { get; } = mappedSlice;
        public bool UsesMappedFrameArena => MappedSlice.IsValid;
    }
}
