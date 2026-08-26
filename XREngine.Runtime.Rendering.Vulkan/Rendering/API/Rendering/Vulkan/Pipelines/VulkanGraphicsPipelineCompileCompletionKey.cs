namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies a compile result by both its immutable pipeline compatibility key
/// and the shader/layout generation held while native creation ran.
/// </summary>
internal readonly record struct VulkanGraphicsPipelineCompileCompletionKey(
    VulkanGraphicsPipelineCompileKey CompileKey,
    long DependencyGeneration);
