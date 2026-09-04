using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen OpenXR queue-submission input. A submission may contain a single eye,
/// an ordered stereo pair, or stereo render plus a dependent publish command.
/// </summary>
internal readonly record struct VulkanOpenXrSubmissionInput(
    CommandBuffer FirstCommandBuffer,
    CommandBuffer SecondCommandBuffer,
    CommandBuffer ThirdCommandBuffer,
    uint CommandBufferCount,
    VulkanSubmissionDiagnosticContext DiagnosticContext,
    bool ForceSynchronousCompletion = false,
    OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket? AdmissionTicket = null)
{
    internal bool IsValid =>
        CommandBufferCount is >= 1 and <= 3 &&
        FirstCommandBuffer.Handle != 0 &&
        (CommandBufferCount == 1 || SecondCommandBuffer.Handle != 0) &&
        (CommandBufferCount < 3 || ThirdCommandBuffer.Handle != 0);
}
