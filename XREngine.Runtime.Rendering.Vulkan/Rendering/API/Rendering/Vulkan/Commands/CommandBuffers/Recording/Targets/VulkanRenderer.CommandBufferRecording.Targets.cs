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
            => _windowPresentSource.HasAnyCompleteBinding();

        private bool IsSwapchainImageEverPresented(uint imageIndex)
            => OutputRuntime.Desktop.ImageEverPresented is not null &&
               imageIndex < OutputRuntime.Desktop.ImageEverPresented.Length &&
               OutputRuntime.Desktop.ImageEverPresented[imageIndex];

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

            if (OutputRuntime.Desktop.Images is null ||
                OutputRuntime.Desktop.ImageViews is null ||
                imageIndex >= OutputRuntime.Desktop.Images.Length ||
                imageIndex >= OutputRuntime.Desktop.ImageViews.Length)
            {
                return default;
            }

            Image swapchainImage = OutputRuntime.Desktop.Images[imageIndex];
            bool imageEverPresented = IsSwapchainImageEverPresented(imageIndex);
            ImageLayout initialSwapchainLayout = ResolveTrackedSwapchainTargetColorLayout(swapchainImage);
            if (initialSwapchainLayout == ImageLayout.Undefined && imageEverPresented)
                initialSwapchainLayout = ImageLayout.PresentSrcKhr;

            VulkanSwapchainDepthResources? depth = CurrentSwapchainDepthResources;
            return new SwapchainRecordingTarget(
                swapchainImage,
                OutputRuntime.Desktop.ImageViews[imageIndex],
                OutputRuntime.Desktop.ImageFormat,
                OutputRuntime.Desktop.Extent,
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

                renderPass = ResourceRuntime.SwapchainRenderPass;
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
            FrameOperationSequence ops,
            int dynamicUiBatchTextOpCount,
            CommandChainSchedule? commandChainSchedule,
            bool preserveSwapchainForOverlay,
            VulkanPrimaryCommandPlan primaryCommandPlan,
            out int recordedSwapchainWriteCount,
            out ImageLayout recordedSwapchainFinalLayout,
            out string recordingDeferredReason,
            out bool queryFrameOpsRequireRerecord,
            bool transitionSwapchainToPresent = true,
            uint? frameDataImageIndexOverride = null,
            OpenXrEyeRenderTargetContext? openXrTargetContext = null,
            bool excludeDesktopSwapchainBarriers = false,
            FramePlan? framePlan = null)
        {
            if (!TryValidateNativeRecordingFramePlan(
                    framePlan,
                    ops,
                    out recordingDeferredReason))
            {
                recordedSwapchainWriteCount = 0;
                recordedSwapchainFinalLayout = ImageLayout.Undefined;
                queryFrameOpsRequireRerecord = false;
                return false;
            }

            VulkanCommandRecordingContext context = new(
                imageIndex,
                commandBuffer,
                dynamicUiBatchTextSecondaryCommandBuffer,
                ops,
                dynamicUiBatchTextOpCount,
                commandChainSchedule,
                preserveSwapchainForOverlay,
                transitionSwapchainToPresent,
                primaryCommandPlan,
                frameDataImageIndexOverride,
                openXrTargetContext,
                excludeDesktopSwapchainBarriers,
                _framePlanner.CaptureSnapshot().RenderGraphPlan,
                framePlan);

            if (!_commandRuntime.Recorder.Prepare(ref context))
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

        private static bool TryValidateNativeRecordingFramePlan(
            FramePlan? framePlan,
            FrameOperationSequence operations,
            out string reason)
        {
            if (framePlan is null)
            {
                reason = "Native command recording requires a sealed frame plan: no frame plan was supplied.";
                return false;
            }

            if (framePlan.TryValidateNativeRecording(operations, out string framePlanReason))
            {
                reason = string.Empty;
                return true;
            }

            reason = $"Native command recording requires a sealed frame plan: {framePlanReason}";
            return false;
        }

    }
}
