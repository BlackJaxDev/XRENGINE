namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies a shader stage and native module frozen in a graphics pipeline request.</summary>
public readonly record struct VulkanPipelineShaderStageDiagnostic(string Stage, ulong ModuleHandle);
