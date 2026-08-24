namespace XREngine.Rendering.Vulkan;

/// <summary>Behavior-only CPU profiling surface for wrapper recording phases.</summary>
internal sealed class VulkanWrapperCpuProfilingPort(VulkanFrameTelemetry telemetry)
{
    internal VulkanCpuStageScope Scope(EVulkanCpuStage stage)
        => new(telemetry, stage);
}
