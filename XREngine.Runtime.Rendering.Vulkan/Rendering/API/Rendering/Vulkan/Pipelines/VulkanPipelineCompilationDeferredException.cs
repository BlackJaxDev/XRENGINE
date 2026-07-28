namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Signals that a compatible cold Vulkan pipeline dependency is already being
/// compiled and that the caller should retry without treating it as a failure.
/// </summary>
internal sealed class VulkanPipelineCompilationDeferredException(string message)
    : Exception(message);
