using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Moves one auto exposure value between pipeline instances through a storage buffer.
/// The OpenXR quad view uses it: the wide pair publishes its exposure, and the inset pair,
/// which renders later in the same frame, copies it instead of metering its own image.
/// </summary>
internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Enqueues a one-thread dispatch that reads the 1x1 exposure texture and writes its value
    /// to <paramref name="channelBuffer"/>. A storage barrier follows, so later work in the
    /// queue, also the consumer's command buffer, sees the value.
    /// </summary>
    internal bool PublishAutoExposureGpu(XRTexture2D exposureTex, XRDataBuffer channelBuffer)
    {
        if (!_resourceRuntime.EnsureAutoExposureComputeResources() ||
            _resourceRuntime.AutoExposurePublishProgram is not { } program)
            return false;

        if (!EnsureExposureStorageUsage(exposureTex))
            return false;

        bool managed = PrepareSharedExposureImageForStorage(exposureTex);
        program.BindImageTexture(
            0,
            exposureTex,
            0,
            false,
            0,
            XRRenderProgram.EImageAccess.ReadWrite,
            XRRenderProgram.EImageFormat.R32F);
        program.BindBuffer(channelBuffer, 1);

        bool enqueued = TryDispatchCompute(program, 1, 1, 1) == ERendererComputeEnqueueStatus.Enqueued;
        if (enqueued)
            EnqueueMemoryBarrier(EMemoryBarrierMask.ShaderStorage);
        FinishSharedExposureImageStorage(exposureTex, managed);
        return enqueued;
    }

    /// <summary>
    /// Enqueues a storage barrier that makes the publisher's buffer write visible, then a
    /// one-thread dispatch that writes the shared value, clamped to the consumer's exposure
    /// bounds, into the consumer's 1x1 exposure texture.
    /// </summary>
    internal bool ConsumeAutoExposureGpu(
        XRDataBuffer channelBuffer,
        XRTexture2D exposureTex,
        float minExposure,
        float maxExposure,
        float fallbackExposure)
    {
        if (!_resourceRuntime.EnsureAutoExposureComputeResources() ||
            _resourceRuntime.AutoExposureConsumeProgram is not { } program)
            return false;

        if (!EnsureExposureStorageUsage(exposureTex))
            return false;

        bool managed = PrepareSharedExposureImageForStorage(exposureTex);
        program.Uniform("MinExposure", minExposure);
        program.Uniform("MaxExposure", maxExposure);
        program.Uniform("FallbackExposure", fallbackExposure);
        program.BindImageTexture(
            0,
            exposureTex,
            0,
            false,
            0,
            XRRenderProgram.EImageAccess.ReadWrite,
            XRRenderProgram.EImageFormat.R32F);
        program.BindBuffer(channelBuffer, 1);

        EnqueueMemoryBarrier(EMemoryBarrierMask.ShaderStorage);
        bool enqueued = TryDispatchCompute(program, 1, 1, 1) == ERendererComputeEnqueueStatus.Enqueued;
        if (enqueued)
            EnqueueMemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.TextureFetch);
        FinishSharedExposureImageStorage(exposureTex, managed);
        return enqueued;
    }

    /// <summary>
    /// Moves a standalone exposure image to GENERAL for storage access. Returns true when the
    /// render graph owns the image layout; the pass that runs the exposure update already
    /// declares read-write use of it, so no transition is recorded here.
    /// </summary>
    private bool PrepareSharedExposureImageForStorage(XRTexture2D exposureTex)
    {
        if (GetOrCreateAPIRenderObject(exposureTex, generateNow: true) is not VkTexture2D vkExposure)
            return false;
        if (vkExposure.UsesAllocatorImage)
            return true;

        Silk.NET.Vulkan.ImageLayout oldLayout = vkExposure.CurrentImageLayout;
        if (oldLayout != Silk.NET.Vulkan.ImageLayout.General)
            vkExposure.TransitionImageLayout(oldLayout, Silk.NET.Vulkan.ImageLayout.General);
        return false;
    }

    /// <summary>Moves a standalone exposure image back to the read-only layout for sampling.</summary>
    private void FinishSharedExposureImageStorage(XRTexture2D exposureTex, bool managedByRenderGraph)
    {
        if (managedByRenderGraph ||
            GetOrCreateAPIRenderObject(exposureTex, generateNow: true) is not VkTexture2D vkExposure)
            return;

        Silk.NET.Vulkan.ImageLayout oldLayout = vkExposure.CurrentImageLayout;
        if (oldLayout != Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal)
            vkExposure.TransitionImageLayout(oldLayout, Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal);
    }
}
