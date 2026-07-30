using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanGraphicsPipelineCompileResult(
    bool Success,
    Pipeline Pipeline,
    string? ErrorMessage,
    double CompileMilliseconds,
    bool Retryable = false);
