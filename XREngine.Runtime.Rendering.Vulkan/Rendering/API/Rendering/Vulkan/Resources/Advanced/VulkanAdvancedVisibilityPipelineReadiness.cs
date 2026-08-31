namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Admission state for the advanced visibility program family. A temporary
/// resource or pipeline absence must not be confused with an unusable family.
/// </summary>
internal enum VulkanAdvancedVisibilityPipelineReadiness : byte
{
    Ready,
    Missing,
    Pending,
    Failed,
}
