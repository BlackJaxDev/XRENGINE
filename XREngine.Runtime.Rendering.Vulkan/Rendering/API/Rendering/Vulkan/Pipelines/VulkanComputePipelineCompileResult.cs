using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanComputePipelineCompileResult(
    bool Success,
    Pipeline Pipeline,
    string? ErrorMessage,
    double CompileMilliseconds,
    bool Retryable = false,
    VkRenderProgram? Owner = null);
