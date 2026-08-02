using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private bool HasLastWindowPresentSourceForSwapchainRefresh()
        {
            XRFrameBuffer? sourceFrameBuffer = _lastWindowPresentFrameBuffer;
            return sourceFrameBuffer is not null &&
                   sourceFrameBuffer.Width > 0 &&
                   sourceFrameBuffer.Height > 0;
        }

        private bool IsSwapchainImageEverPresented(uint imageIndex)
            => _swapchainImageEverPresented is not null &&
               imageIndex < _swapchainImageEverPresented.Length &&
               _swapchainImageEverPresented[imageIndex];

        private ImageLayout ResolveTrackedSwapchainTargetColorLayout(Image image)
        {
            if (image.Handle == 0)
                return ImageLayout.Undefined;

            ImageSubresourceRange colorRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            return TryGetTrackedImageLayout(image, colorRange, out ImageLayout trackedLayout)
                ? trackedLayout
                : ImageLayout.Undefined;
        }

        private SwapchainRecordingTarget ResolveSwapchainRecordingTarget(
            uint imageIndex,
            OpenXrEyeRenderTargetContext? openXrTargetContext)
        {
            if (openXrTargetContext is { } openXrTarget && openXrTarget.IsValid)
            {
                ImageLayout initialColorLayout = ResolveTrackedSwapchainTargetColorLayout(openXrTarget.Image);
                return new SwapchainRecordingTarget(
                    openXrTarget.Image,
                    openXrTarget.ImageView,
                    openXrTarget.ImageFormat,
                    openXrTarget.Extent,
                    openXrTarget.DepthImage,
                    openXrTarget.DepthView,
                    openXrTarget.DepthFormat,
                    openXrTarget.DepthAspect,
                    initialColorLayout,
                    ImageEverPresentedAtRecordStart: false);
            }

            if (swapChainImages is null ||
                swapChainImageViews is null ||
                imageIndex >= swapChainImages.Length ||
                imageIndex >= swapChainImageViews.Length)
            {
                return default;
            }

            Image swapchainImage = swapChainImages[imageIndex];
            bool imageEverPresented = IsSwapchainImageEverPresented(imageIndex);
            ImageLayout initialSwapchainLayout = ResolveTrackedSwapchainTargetColorLayout(swapchainImage);
            if (initialSwapchainLayout == ImageLayout.Undefined && imageEverPresented)
                initialSwapchainLayout = ImageLayout.PresentSrcKhr;

            VulkanSwapchainDepthResources? depth = CurrentSwapchainDepthResources;
            return new SwapchainRecordingTarget(
                swapchainImage,
                swapChainImageViews[imageIndex],
                swapChainImageFormat,
                swapChainExtent,
                depth?.Image ?? default,
                depth?.View ?? default,
                depth?.Format ?? default,
                depth?.Aspect ?? default,
                initialSwapchainLayout,
                imageEverPresented);
        }

        private bool TryResolveGraphicsPipelinePrewarmTarget(
            XRFrameBuffer? target,
            int passIndex,
            in FrameOpContext context,
            in SwapchainRecordingTarget swapchainTarget,
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
                useDynamicRendering = UseDynamicRenderingRenderTargets && swapchainTarget.IsValid;
                if (useDynamicRendering)
                {
                    dynamicRenderingFormats = CreateSwapchainDynamicRenderingFormatSignature(
                        swapchainTarget.ImageFormat,
                        swapchainTarget.DepthFormat);
                    return true;
                }

                renderPass = _renderPass;
                if (renderPass.Handle != 0)
                    return true;

                reason = "legacy swapchain render pass is unavailable";
                return false;
            }

            VkFrameBuffer? vkFrameBuffer = GenericToAPI<VkFrameBuffer>(target);
            if (vkFrameBuffer is null)
            {
                reason = $"target '{target.Name ?? "<unnamed>"}' has no Vulkan framebuffer";
                return false;
            }

            vkFrameBuffer.EnsureCurrent();
            ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(target, vkFrameBuffer);
            FrameBufferAttachmentSignature[] attachmentSignature = vkFrameBuffer.ResolveAttachmentSignatureForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                CompiledRenderGraph.Synchronization,
                preserveTrackedClearLoads: false);
            depthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(attachmentSignature);

            if (UseDynamicRenderingRenderTargets)
            {
                useDynamicRendering = true;
                uint viewMask = vkFrameBuffer.MultiviewViewMask;
                dynamicRenderingFormats = CreateDynamicRenderingFormatSignature(
                    attachmentSignature,
                    viewMask,
                    VulkanDynamicRenderingUtilities.ResolveLayerCount(vkFrameBuffer.FramebufferLayers, viewMask));
                return true;
            }

            renderPass = vkFrameBuffer.ResolveRenderPassForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                CompiledRenderGraph.Synchronization,
                preserveTrackedClearLoads: false);
            if (renderPass.Handle != 0)
                return true;

            reason = $"target '{target.Name ?? "<unnamed>"}' has no compatible legacy render pass";
            return false;
        }

        private bool TryRecordCommandBuffer(
            uint imageIndex,
            CommandBuffer commandBuffer,
            CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer,
            FrameOp[] ops,
            int dynamicUiBatchTextOpCount,
            CommandChainSchedule? commandChainSchedule,
            bool preserveSwapchainForOverlay,
            out int recordedSwapchainWriteCount,
            out ImageLayout recordedSwapchainFinalLayout,
            out string recordingDeferredReason,
            out bool queryFrameOpsRequireRerecord,
            bool transitionSwapchainToPresent = true,
            uint? frameDataImageIndexOverride = null,
            OpenXrEyeRenderTargetContext? openXrTargetContext = null,
            bool excludeDesktopSwapchainBarriers = false)
        {
            VulkanCommandRecordingContext context = new(
                imageIndex,
                commandBuffer,
                dynamicUiBatchTextSecondaryCommandBuffer,
                ops,
                dynamicUiBatchTextOpCount,
                commandChainSchedule,
                preserveSwapchainForOverlay,
                transitionSwapchainToPresent,
                frameDataImageIndexOverride,
                openXrTargetContext,
                excludeDesktopSwapchainBarriers,
                _renderGraphRuntime.CurrentPlan);

            if (!_commandRecorder.Prepare(ref context))
            {
                recordedSwapchainWriteCount = context.RecordedSwapchainWriteCount;
                recordedSwapchainFinalLayout = context.RecordedSwapchainFinalLayout;
                recordingDeferredReason = context.RecordingDeferredReason;
                queryFrameOpsRequireRerecord = context.QueryFrameOpsRequireRerecord;
                return false;
            }

            bool recorded = RecordCommandBufferLifecycle(ref context);
            recordedSwapchainWriteCount = context.RecordedSwapchainWriteCount;
            recordedSwapchainFinalLayout = context.RecordedSwapchainFinalLayout;
            recordingDeferredReason = context.RecordingDeferredReason;
            queryFrameOpsRequireRerecord = context.QueryFrameOpsRequireRerecord;
            return recorded;
        }

    }
}
