using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        private Result SubmitAcquireSemaphoreBridge(Semaphore acquireSemaphore, ulong signalTimelineValue)
            => SubmitAcquireSemaphoreBridge(acquireSemaphore, signalTimelineValue, default, null, 0);

        private Result SubmitAcquireSemaphoreBridge(
            Semaphore acquireSemaphore,
            ulong signalTimelineValue,
            Semaphore signalPresentSemaphore,
            CommandBuffer* commandBuffers,
            uint commandBufferCount)
        {
            uint signalSemaphoreCount = signalPresentSemaphore.Handle != 0 ? 2u : 1u;
            ulong* signalValues = stackalloc ulong[2] { signalTimelineValue, 0UL };
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

            return _commandRuntime.SubmitToQueueTracked(
                Api,
                _deviceContext,
                _frameTelemetry,
                _deviceContext.GraphicsQueue,
                ref submit,
                default,
                nameof(SubmitAcquireSemaphoreBridge));
        }

    }
}
