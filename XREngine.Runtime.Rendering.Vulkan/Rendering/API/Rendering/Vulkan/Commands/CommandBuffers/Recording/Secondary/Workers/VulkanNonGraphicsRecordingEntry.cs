using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal struct VulkanNonGraphicsRecordingEntry
{
    internal CommandChain? Chain;
    internal CommandBuffer SecondaryBuffer;
    internal int WorkerIndex;
}
