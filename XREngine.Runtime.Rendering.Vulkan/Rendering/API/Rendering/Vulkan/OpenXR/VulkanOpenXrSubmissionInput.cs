using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen OpenXR queue-submission input. OpenXR currently submits either one
/// eye command buffer or one ordered stereo pair.
/// </summary>
internal readonly record struct VulkanOpenXrSubmissionInput(
    CommandBuffer FirstCommandBuffer,
    CommandBuffer SecondCommandBuffer,
    uint CommandBufferCount,
    VulkanSubmissionDiagnosticContext DiagnosticContext,
    bool ForceSynchronousCompletion = false)
{
    internal bool IsValid =>
        CommandBufferCount is 1 or 2 &&
        FirstCommandBuffer.Handle != 0 &&
        (CommandBufferCount == 1 || SecondCommandBuffer.Handle != 0);
}
