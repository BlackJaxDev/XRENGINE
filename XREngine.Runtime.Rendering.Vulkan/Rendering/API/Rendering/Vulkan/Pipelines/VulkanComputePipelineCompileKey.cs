namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable identity for one generation-specific compute pipeline request.</summary>
internal readonly record struct VulkanComputePipelineCompileKey(
    long ProgramBindingId,
    ulong ProgramFingerprint,
    ulong ProgramLinkGeneration,
    ulong PipelineLayoutHandle,
    long DependencyGeneration);
