using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal unsafe readonly struct VulkanDescriptorHeapStorage(
    Buffer buffer,
    DeviceMemory memory,
    void* mapped,
    ulong size,
    ulong deviceAddress,
    Buffer stagingBuffer,
    DeviceMemory stagingMemory,
    bool requiresCopy)
{
    public Buffer Buffer { get; } = buffer;
    public DeviceMemory Memory { get; } = memory;
    public void* Mapped { get; } = mapped;
    public ulong Size { get; } = size;
    public ulong DeviceAddress { get; } = deviceAddress;
    public Buffer StagingBuffer { get; } = stagingBuffer;
    public DeviceMemory StagingMemory { get; } = stagingMemory;
    public bool RequiresCopy { get; } = requiresCopy;
    public bool IsReady => Buffer.Handle != 0 && Memory.Handle != 0 && Mapped != null && Size > 0 && DeviceAddress != 0;
}
