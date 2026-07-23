using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record BufferCopyOp(
        int PassIndex,
        VkDataBuffer SourceOwner,
        Buffer SourceBuffer,
        ulong SourceOffset,
        VkDataBuffer DestinationOwner,
        Buffer DestinationBuffer,
        ulong DestinationOffset,
        ulong ByteCount,
        string Label,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);
}
