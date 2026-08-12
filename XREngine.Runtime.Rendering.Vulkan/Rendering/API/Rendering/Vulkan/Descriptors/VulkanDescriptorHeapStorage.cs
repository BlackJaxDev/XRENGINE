using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal readonly struct VulkanDescriptorHeapStorage(
    Buffer buffer,
    DeviceMemory memory,
    VulkanMappedMemorySlice mappedMemorySlice,
    ulong size,
    ulong deviceAddress,
    Buffer stagingBuffer,
    DeviceMemory stagingMemory,
    bool requiresCopy)
{
    public Buffer Buffer { get; } = buffer;
    public DeviceMemory Memory { get; } = memory;
    public VulkanMappedMemorySlice MappedMemorySlice { get; } = mappedMemorySlice;
    public ulong Size { get; } = size;
    public ulong DeviceAddress { get; } = deviceAddress;
    public Buffer StagingBuffer { get; } = stagingBuffer;
    public DeviceMemory StagingMemory { get; } = stagingMemory;
    public bool RequiresCopy { get; } = requiresCopy;
    public bool IsReady => Buffer.Handle != 0 && Memory.Handle != 0 && MappedMemorySlice.Buffer.Handle != 0 && Size > 0 && DeviceAddress != 0;
}
