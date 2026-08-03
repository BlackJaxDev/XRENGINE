namespace XREngine.Rendering;

/// <summary>
/// Bounded diagnostic identity for one conservative Vulkan binding-artifact
/// fallback observed in the latest completed profiler frame.
/// </summary>
public readonly record struct VulkanProgramBindingArtifactFallbackSample(
    EVulkanProgramBindingArtifactFallbackReason Reason,
    string? MeshName,
    string? MaterialName,
    string? ProgramName,
    string? Detail);
