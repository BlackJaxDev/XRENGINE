namespace XREngine.Rendering.Vulkan;

/// <summary>Retryable compute pipeline admission state; pending is not a missing dispatch.</summary>
internal enum VulkanComputePipelineReadiness
{
    Ready,
    Pending,
    Failed,
}
