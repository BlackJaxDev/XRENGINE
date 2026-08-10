using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private bool SubmitAndWaitOpenXrCommandBuffer(
        CommandBuffer commandBuffer,
        out bool commandBufferCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        VulkanOpenXrSubmissionInput input = new(
            commandBuffer,
            default,
            1,
            diagnosticContext);
        VulkanOpenXrSubmissionResult result =
            _commandRuntime.SubmitAndWaitOpenXr(in input);
        commandBufferCompleted = result.CommandBuffersCompleted;
        return result.Succeeded;
    }

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer firstCommandBuffer,
        CommandBuffer secondCommandBuffer,
        out bool commandBuffersCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        VulkanOpenXrSubmissionInput input = new(
            firstCommandBuffer,
            secondCommandBuffer,
            2,
            diagnosticContext);
        VulkanOpenXrSubmissionResult result =
            _commandRuntime.SubmitAndWaitOpenXr(in input);
        commandBuffersCompleted = result.CommandBuffersCompleted;
        return result.Succeeded;
    }

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool commandBufferCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
        => SubmitAndWaitOpenXrCommandBuffers(
            commandBuffers,
            commandBufferCount,
            out commandBufferCompleted,
            out _,
            out _,
            diagnosticContext);

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool commandBufferCompleted,
        out EVulkanQueueSubmissionDisposition submissionDisposition,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        commandBufferCompleted = false;
        submissionDisposition = EVulkanQueueSubmissionDisposition.NotSubmitted;
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        if (commandBuffers is null || commandBufferCount is 0 or > 2)
            return false;

        VulkanOpenXrSubmissionInput input = new(
            commandBuffers[0],
            commandBufferCount == 2 ? commandBuffers[1] : default,
            commandBufferCount,
            diagnosticContext);
        VulkanOpenXrSubmissionResult result =
            _commandRuntime.SubmitAndWaitOpenXr(in input);
        commandBufferCompleted = result.CommandBuffersCompleted;
        submissionDisposition = result.SubmissionDisposition;
        injectedFailureStage = result.InjectedFailureStage;
        return result.Succeeded;
    }
}
