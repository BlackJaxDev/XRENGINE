using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed class GpuRenderStatsReadbackSlot
{
    public Buffer StagingBuffer;
    public DeviceMemory StagingMemory;
    public ulong CapacityBytes;
    public uint ByteCount;
    public uint ElementCount;
    public CommandPool CommandPool;
    public CommandBuffer CommandBuffer;
    public Fence Fence;
    public bool Active;
    public bool PublishDraws;
    public bool PublishTriangles;
    public GpuRenderStatsReadbackKind Kind;
    public string SourceName = string.Empty;
    public ulong SourceHandle;
}
