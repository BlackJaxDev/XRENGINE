using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Submits the three retained primaries that precede the normal final frame
    /// submission. The ordinary primary remains the sole owner of target acquire,
    /// completion, and presentation synchronization.
    /// </summary>
    internal unsafe void SubmitAdvancedQueueOverlapPrefixes(
        VulkanAdvancedQueueOverlapSlot slot,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!slot.Recorded)
            throw new InvalidOperationException("Advanced queue-overlap prefixes must be fully recorded before submission.");
        if (slot.Prefix.Handle == 0 || slot.Classification.Handle == 0 || slot.Independent.Handle == 0)
            throw new InvalidOperationException("Advanced queue-overlap slot has incomplete command-buffer resources.");
        if (!DeviceContext.HasSecondaryGraphicsQueue)
            throw new InvalidOperationException("Advanced queue-overlap requires a distinct secondary graphics queue.");

        Queue graphicsQueue = DeviceContext.GraphicsQueue;
        Queue classificationQueue = DeviceContext.SecondaryGraphicsQueue;
        if (graphicsQueue.Handle == 0 || classificationQueue.Handle == 0 ||
            graphicsQueue.Handle == classificationQueue.Handle)
        {
            throw new InvalidOperationException("Advanced queue-overlap requires two distinct graphics queue handles.");
        }

        try
        {
            VulkanSubmissionDiagnosticContext prefixDiagnostics = diagnosticContext with
            {
                SubmissionKind = "AdvancedQueueOverlapPrefix",
                FrameOpKind = "AdvancedQueueOverlap.G0",
            };
            VulkanSubmissionReceipt prefixReceipt = SubmitAdvancedQueueOverlapPrimary(
                graphicsQueue,
                slot.Prefix,
                slot.PrefixFence,
                waitSemaphore: default,
                waitStage: 0,
                signalSemaphore: slot.Ready,
                in prefixDiagnostics,
                "AdvancedQueueOverlap.Prefix");
            slot.PrefixAccepted = prefixReceipt.SubmissionAccepted;
            ThrowIfAdvancedQueueOverlapRejected(slot, prefixReceipt, "prefix");

            VulkanSubmissionDiagnosticContext classificationDiagnostics = diagnosticContext with
            {
                SubmissionKind = "AdvancedQueueOverlapClassification",
                FrameOpKind = "AdvancedQueueOverlap.C",
            };
            VulkanSubmissionReceipt classificationReceipt = SubmitAdvancedQueueOverlapPrimary(
                classificationQueue,
                slot.Classification,
                slot.ClassificationFence,
                slot.Ready,
                PipelineStageFlags.AllCommandsBit,
                slot.Classified,
                in classificationDiagnostics,
                "AdvancedQueueOverlap.Classification");
            slot.ClassificationAccepted = classificationReceipt.SubmissionAccepted;
            ThrowIfAdvancedQueueOverlapRejected(slot, classificationReceipt, "classification");

            // This queue submission intentionally has no binary wait. Queue order
            // makes it follow G0, while C can overlap it on the second graphics queue.
            VulkanSubmissionDiagnosticContext independentDiagnostics = diagnosticContext with
            {
                SubmissionKind = "AdvancedQueueOverlapIndependent",
                FrameOpKind = "AdvancedQueueOverlap.G1",
            };
            VulkanSubmissionReceipt independentReceipt = SubmitAdvancedQueueOverlapPrimary(
                graphicsQueue,
                slot.Independent,
                slot.IndependentFence,
                waitSemaphore: default,
                waitStage: 0,
                signalSemaphore: default,
                in independentDiagnostics,
                "AdvancedQueueOverlap.Independent");
            slot.IndependentAccepted = independentReceipt.SubmissionAccepted;
            ThrowIfAdvancedQueueOverlapRejected(slot, independentReceipt, "independent");
        }
        catch
        {
            if (slot.HasAcceptedPrefixes)
                CompleteAdvancedQueueOverlapSubmission(slot, finalAccepted: false);
            throw;
        }
    }

    private unsafe VulkanSubmissionReceipt SubmitAdvancedQueueOverlapPrimary(
        Queue queue,
        CommandBuffer commandBuffer,
        Fence fence,
        Semaphore waitSemaphore,
        PipelineStageFlags waitStage,
        Semaphore signalSemaphore,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        string caller)
    {
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[1] { commandBuffer };
        Semaphore* waitSemaphores = stackalloc Semaphore[1] { waitSemaphore };
        PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[1] { waitStage };
        Semaphore* signalSemaphores = stackalloc Semaphore[1] { signalSemaphore };
        bool hasWait = waitSemaphore.Handle != 0;
        bool hasSignal = signalSemaphore.Handle != 0;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = commandBuffers,
            WaitSemaphoreCount = hasWait ? 1U : 0U,
            PWaitSemaphores = hasWait ? waitSemaphores : null,
            PWaitDstStageMask = hasWait ? waitStages : null,
            SignalSemaphoreCount = hasSignal ? 1U : 0U,
            PSignalSemaphores = hasSignal ? signalSemaphores : null,
        };
        return SubmitToQueueTrackedWithDisposition(
            queue,
            ref submitInfo,
            fence,
            in diagnosticContext,
            out _,
            out _,
            caller);
    }

    private void ThrowIfAdvancedQueueOverlapRejected(
        VulkanAdvancedQueueOverlapSlot slot,
        in VulkanSubmissionReceipt receipt,
        string phase)
    {
        if (receipt.SubmissionAccepted && receipt.LifetimePinsTransferred &&
            receipt.PostSubmissionPublicationSucceeded)
            return;

        CompleteAdvancedQueueOverlapSubmission(slot, finalAccepted: false);
        throw new InvalidOperationException(
            $"Advanced queue-overlap {phase} submission could not publish safe successor state " +
            $"(result={receipt.Result}, accepted={receipt.SubmissionAccepted}, " +
            $"pinsTransferred={receipt.LifetimePinsTransferred}, published={receipt.PostSubmissionPublicationSucceeded}).");
    }
}
