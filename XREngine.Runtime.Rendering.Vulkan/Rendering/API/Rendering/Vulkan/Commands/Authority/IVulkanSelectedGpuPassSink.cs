using Silk.NET.Vulkan;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Records selected production pass timestamps into a caller-owned diagnostic session.</summary>
public interface IVulkanSelectedGpuPassSink
{
    void BeginFrame(CommandBuffer commandBuffer, uint frameSlot, ulong sourceFrameId);
    int BeginProductionPass(CommandBuffer commandBuffer, uint frameSlot, int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata);
    void EndPass(CommandBuffer commandBuffer, uint frameSlot, int startQuery);
}
