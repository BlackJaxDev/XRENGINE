using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandBuffer EnsureCommandBufferRecorded(
            uint imageIndex,
            bool preserveSwapchainForOverlay,
            out string recordingDeferredReason,
            out CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer,
            out int dynamicUiBatchTextOverlayOpCount,
            out FrameOp[] dynamicUiBatchTextOverlayOps,
            out ulong dynamicUiBatchTextOverlaySignature,
            out CommandBufferCacheVariant? dynamicUiBatchTextOverlayVariant,
            out CommandBuffer textureUploadCommandBuffer,
            out CommandPool textureUploadCommandPool,
            out ImageLayout swapchainLayoutAfterCommandBuffer,
            out long commandBufferDirtyGenerationAfterRecord)
        {
            VulkanCommandSchedulingContext<CommandBufferCacheVariant> schedulingContext =
                _commandScheduler.Capture<CommandBufferCacheVariant>(
                    imageIndex,
                    preserveSwapchainForOverlay,
                    _renderGraphRuntime.CurrentPlan);

            CommandBuffer commandBuffer =
                ScheduleCommandBufferLifecycle(ref schedulingContext);
            recordingDeferredReason = schedulingContext.RecordingDeferredReason;
            dynamicUiBatchTextSecondaryCommandBuffer =
                schedulingContext.DynamicUiSecondaryCommandBuffer;
            dynamicUiBatchTextOverlayOpCount =
                schedulingContext.DynamicUiOverlayOperationCount;
            dynamicUiBatchTextOverlayOps =
                schedulingContext.DynamicUiOverlayOperations;
            dynamicUiBatchTextOverlaySignature =
                schedulingContext.DynamicUiOverlaySignature;
            dynamicUiBatchTextOverlayVariant =
                schedulingContext.DynamicUiOverlayVariant;
            textureUploadCommandBuffer =
                schedulingContext.TextureUploadCommandBuffer;
            textureUploadCommandPool = schedulingContext.TextureUploadCommandPool;
            swapchainLayoutAfterCommandBuffer =
                schedulingContext.SwapchainLayoutAfterCommandBuffer;
            commandBufferDirtyGenerationAfterRecord =
                schedulingContext.CommandBufferDirtyGenerationAfterRecord;
            return commandBuffer;
        }

        private CommandBuffer ScheduleCommandBufferLifecycle(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context)
        {
            ResetCommandBufferLifecycle(ref context);
            if (!TryInitializeCommandBufferLifecycle(
                    ref context,
                    out CommandBufferLifecycleState state))
            {
                return default;
            }

            PrepareCommandBufferFrameOperations(ref state);
            if (!TryRegisterCommandBufferFrameDataManifest(
                    ref context,
                    ref state))
            {
                return default;
            }

            return SchedulePreparedCommandBufferLifecycle(
                ref context,
                ref state);
        }
    }
}
