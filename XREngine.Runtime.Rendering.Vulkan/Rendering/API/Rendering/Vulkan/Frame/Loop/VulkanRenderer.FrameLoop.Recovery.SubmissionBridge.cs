using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        private unsafe VulkanSubmissionReceipt SubmitAcquireSemaphoreBridge(
            Semaphore acquireSemaphore,
            ulong minimumTimelineValue,
            out ulong signalTimelineValue)
            => SubmitAcquireSemaphoreBridge(
                acquireSemaphore,
                minimumTimelineValue,
                default,
                null,
                0,
                out signalTimelineValue);

        private unsafe VulkanSubmissionReceipt SubmitAcquireSemaphoreBridge(
            Semaphore acquireSemaphore,
            ulong minimumTimelineValue,
            Semaphore signalPresentSemaphore,
            CommandBuffer* commandBuffers,
            uint commandBufferCount,
            out ulong signalTimelineValue)
        {
            uint signalSemaphoreCount = signalPresentSemaphore.Handle != 0 ? 2u : 1u;
            ulong* signalValues = stackalloc ulong[2] { 0UL, 0UL };
            ulong* waitValues = stackalloc ulong[1] { 0UL };
            Semaphore* waitSemaphores = stackalloc Semaphore[1] { acquireSemaphore };
            Semaphore* signalSemaphores = stackalloc Semaphore[2] { _commandRuntime.Synchronization._graphicsTimelineSemaphore, signalPresentSemaphore };
            PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[1] { PipelineStageFlags.TopOfPipeBit };

            TimelineSemaphoreSubmitInfo timelineInfo = new()
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 1,
                PWaitSemaphoreValues = waitValues,
                SignalSemaphoreValueCount = signalSemaphoreCount,
                PSignalSemaphoreValues = signalValues,
            };

            SubmitInfo submit = new()
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = commandBufferCount,
                PCommandBuffers = commandBufferCount == 0 ? null : commandBuffers,
                SignalSemaphoreCount = signalSemaphoreCount,
                PSignalSemaphores = signalSemaphores,
            };

            VulkanSubmissionDiagnosticContext diagnosticContext = new()
            {
                SubmissionKind = "DesktopAcquireRecovery",
                FrameOpKind = "Recovery",
                OutputTargetName = "Swapchain",
            };
            return _commandRuntime.SubmitToGraphicsTimelineTrackedWithDisposition(
                _deviceContext.GraphicsQueue,
                ref submit,
                default,
                _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                minimumTimelineValue,
                in diagnosticContext,
                out signalTimelineValue,
                out _,
                out _,
                nameof(SubmitAcquireSemaphoreBridge));
        }

    }
}
