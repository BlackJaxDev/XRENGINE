using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Narrow renderer-free command surface used by query wrappers.</summary>
internal sealed class VulkanQueryCommandService(VulkanCommandRuntime commandRuntime)
{
    internal VulkanTrackedCommandEncoder Encoder
        => commandRuntime.CreateQueryCommandEncoder();

    internal void Track(
        CommandBuffer commandBuffer,
        ObjectType objectType,
        ulong handle,
        string owner)
        => commandRuntime.TrackVulkanCommandBufferResource(
            commandBuffer,
            objectType,
            handle,
            owner);

    internal void WriteTimestamp2(
        CommandBuffer commandBuffer,
        PipelineStageFlags2 stage,
        QueryPool queryPool,
        uint query)
        => commandRuntime.WriteTimestamp2(
            commandBuffer,
            stage,
            queryPool,
            query);
}
