using System;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public partial class VulkanRenderer
    {
        /// <summary>
        /// Inserts a memory barrier into the current frame, ensuring proper synchronization of memory accesses based on the specified barrier mask.
        /// </summary>
        /// <param name="mask">The memory barrier mask specifying the types of memory accesses to synchronize.</param>
        public override void MemoryBarrier(EMemoryBarrierMask mask)
        {
            if (mask == EMemoryBarrierMask.None)
                return;

            FrameOpContext context = CaptureFrameOpContextOrLastActive();
            int passIndex = EnsureValidPassIndex(
                RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
                "MemoryBarrier",
                context.PassMetadata);

            if (passIndex == int.MinValue)
            {
                ActiveState.RegisterMemoryBarrier(mask);
                MarkCommandBuffersDirty();
                return;
            }

            EnqueueFrameOp(new MemoryBarrierOp(passIndex, mask, context));
        }

        /// <summary>
        /// Publishes the attachments of the specified frame buffer for sampling in subsequent rendering passes.
        /// </summary>
        /// <param name="frameBuffer">The frame buffer whose attachments are to be published for sampling.</param>
        public override void PublishFrameBufferAttachmentsForSampling(XRFrameBuffer frameBuffer)
        {
            ArgumentNullException.ThrowIfNull(frameBuffer);

            FrameOpContext context;
            int passIndex;
            if (TryGetLastFrameOpForTarget(frameBuffer, out FrameOp lastWriter))
            {
                context = lastWriter.Context;
                passIndex = lastWriter.PassIndex;
            }
            else
            {
                context = CaptureFrameOpContextOrLastActive();
                passIndex = EnsureValidPassIndex(
                    RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
                    "PublishFrameBufferAttachmentsForSampling",
                    context.PassMetadata);
            }

            EnqueueFrameOp(new PublishFramebufferForSamplingOp(passIndex, frameBuffer, context));
        }
    }
}
