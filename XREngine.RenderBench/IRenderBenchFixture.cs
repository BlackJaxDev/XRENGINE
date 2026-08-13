using Silk.NET.Vulkan;
using XREngine.Rendering.Profiling;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Prepared deterministic workload recorded inside the explicit-target frame callback.</summary>
public interface IRenderBenchFixture : IDisposable
{
    RenderBenchFixtureDefinition Definition { get; }
    RenderBenchFixtureManifest Manifest { get; }
    RenderBenchWorkCounters Counters { get; }
    long WorkerAllocatedBytes { get; }
    void Prepare(VulkanExplicitTargetRendererHost host, RenderProfileRecipe recipe);
    void BeginCapture();
    void EndCapture();
    void RecordFrame(Vk api, CommandBuffer commandBuffer, VulkanRenderFrameTarget target);
}
