using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    private VulkanPipelineVariantManifest GetOrBuildPipelineVariantManifest(
        VulkanCompiledRenderGraphPlan plan,
        FrameOperationSequence operations,
        EMeshSubmissionStrategy submissionStrategy,
        bool dynamicRendering,
        ulong recordingStructuralSignature)
        => ResourceRuntime.PipelineManager.GetOrBuildVariantManifest(
            plan,
            operations,
            submissionStrategy,
            dynamicRendering,
            recordingStructuralSignature);

    private bool TryResolveGraphicsPipelinePrewarmTarget(
        XRFrameBuffer? target,
        int passIndex,
        in FrameOpContext context,
        in SwapchainRecordingTarget swapchainTarget,
        bool useDynamicRenderingRenderTargets,
        VulkanCompiledRenderGraph compiledGraph,
        out bool useDynamicRendering,
        out RenderPass renderPass,
        out DynamicRenderingFormatSignature dynamicRenderingFormats,
        out bool depthStencilReadOnly,
        out string reason)
    {
        useDynamicRendering = false;
        renderPass = default;
        dynamicRenderingFormats = default;
        depthStencilReadOnly = false;
        reason = string.Empty;

        if (target is null)
        {
            useDynamicRendering = useDynamicRenderingRenderTargets && swapchainTarget.IsValid;
            if (useDynamicRendering)
            {
                dynamicRenderingFormats = CreateSwapchainDynamicRenderingFormatSignature(
                    swapchainTarget.ImageFormat,
                    swapchainTarget.DepthFormat);
                return true;
            }

            renderPass = ResourceRuntime.SwapchainRenderPass;
            if (renderPass.Handle != 0)
                return true;

            reason = "legacy swapchain render pass is unavailable";
            return false;
        }

        VkFrameBuffer? frameBuffer = GenericToAPI<VkFrameBuffer>(target);
        if (frameBuffer is null)
        {
            reason = $"target '{target.Name ?? "<unnamed>"}' has no prepared Vulkan framebuffer";
            return false;
        }

        if (!frameBuffer.IsActive)
        {
            reason = $"target '{target.Name ?? "<unnamed>"}' has no prepared native framebuffer state";
            return false;
        }
        ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(target, frameBuffer);
        FrameBufferAttachmentSignature[] attachmentSignature =
            frameBuffer.ResolveAttachmentSignatureForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                compiledGraph.Synchronization,
                preserveTrackedClearLoads: false);
        depthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(attachmentSignature);

        if (useDynamicRenderingRenderTargets)
        {
            useDynamicRendering = true;
            uint viewMask = frameBuffer.MultiviewViewMask;
            dynamicRenderingFormats = CreateDynamicRenderingFormatSignature(
                attachmentSignature,
                viewMask,
                VulkanDynamicRenderingUtilities.ResolveLayerCount(
                    frameBuffer.FramebufferLayers,
                    viewMask));
            return true;
        }

        renderPass = frameBuffer.ResolveRenderPassForPass(
            passIndex,
            context.PassMetadata,
            trackedLayouts,
            compiledGraph.Synchronization,
            preserveTrackedClearLoads: false);
        if (renderPass.Handle != 0)
            return true;

        reason = $"target '{target.Name ?? "<unnamed>"}' has no compatible legacy render pass";
        return false;
    }
}
