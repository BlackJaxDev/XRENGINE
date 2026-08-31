using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Actual scene command capacity and native allocation at one boundary observation.</summary>
public sealed record RenderBenchNativeBufferStressCapacity
{
    public uint TotalCommandCount { get; init; }
    public uint AllocatedMaxCommandCount { get; init; }
    public VulkanNativeBufferDiagnosticDescription Binding { get; init; }
}
