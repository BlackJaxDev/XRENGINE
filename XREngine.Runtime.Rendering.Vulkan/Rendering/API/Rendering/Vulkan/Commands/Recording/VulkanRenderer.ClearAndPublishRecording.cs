using System;
using System.Buffers;
using System.Collections.Generic;
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
        internal void RecordClearOp(
            CommandBuffer commandBuffer,
            uint imageIndex,
            ClearOp op,
            Rect2D activeRenderArea,
            in SwapchainRecordingTarget swapchainTarget,
            uint activeRenderLayerCount = 0u,
            uint activeRenderViewMask = 0u,
            bool suppressColorClear = false)
        {
            _ = imageIndex;
            bool clearColor = op.ClearColor && !suppressColorClear;

            Extent2D targetExtent = op.Target is null
                ? (swapchainTarget.IsValid ? swapchainTarget.Extent : swapChainExtent)
                : new Extent2D(Math.Max(op.Target.Width, 1u), Math.Max(op.Target.Height, 1u));

            Rect2D clearArea = ClampRectToExtent(
                op.Rect,
                targetExtent);
            clearArea = ClampRectToRenderArea(clearArea, activeRenderArea);

            // Vulkan validation requires non-zero extent for vkCmdClearAttachments.
            if (clearArea.Extent.Width == 0 || clearArea.Extent.Height == 0)
                return;

            VkFrameBuffer? clearTargetFrameBuffer = op.Target is not null
                ? GenericToAPI<VkFrameBuffer>(op.Target)
                : null;
            clearTargetFrameBuffer?.EnsureCurrent();
            uint clearLayerCount = ResolveClearRectLayerCount(op.Target, clearTargetFrameBuffer, activeRenderLayerCount, activeRenderViewMask);

            if (clearLayerCount > 1u)
            {
                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                    $"Vulkan.CmdClearAttachments.Layered.{op.Target?.Name ?? "<swapchain>"}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] CmdClearAttachments layered clear target='{0}' layers={1} activeLayers={2} activeViewMask=0x{3:X} fboLayers={4} fboViewMask=0x{5:X}",
                    op.Target?.Name ?? "<swapchain>",
                    clearLayerCount,
                    activeRenderLayerCount,
                    activeRenderViewMask,
                    clearTargetFrameBuffer?.FramebufferLayers ?? 0u,
                    clearTargetFrameBuffer?.MultiviewViewMask ?? 0u);
                }
            }

            ClearRect clearRect = new()
            {
                Rect = clearArea,
                BaseArrayLayer = 0,
                LayerCount = clearLayerCount
            };

            ClearRect* rectPtr = stackalloc ClearRect[1];
            rectPtr[0] = clearRect;

            if (op.Target is null)
            {
                // Swapchain: single color attachment + depth.
                ClearAttachment* attachments = stackalloc ClearAttachment[2];
                uint count = 0;

                if (clearColor)
                {
                    attachments[count++] = new ClearAttachment
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = 0,
                        ClearValue = new ClearValue
                        {
                            Color = new ClearColorValue
                            {
                                Float32_0 = op.Color.R,
                                Float32_1 = op.Color.G,
                                Float32_2 = op.Color.B,
                                Float32_3 = op.Color.A
                            }
                        }
                    };
                }

                if (op.ClearDepth || op.ClearStencil)
                {
                    ImageAspectFlags requestedAspects = ImageAspectFlags.None;
                    if (op.ClearDepth)
                        requestedAspects |= ImageAspectFlags.DepthBit;
                    if (op.ClearStencil)
                        requestedAspects |= ImageAspectFlags.StencilBit;

                    // Only emit aspects actually supported by the swapchain depth attachment view.
                    // Example: VK_FORMAT_D32_SFLOAT does not support stencil clears.
                    ImageAspectFlags depthAspect = swapchainTarget.IsValid ? swapchainTarget.DepthAspect : _swapchainDepthAspect;
                    ImageAspectFlags aspects = requestedAspects & depthAspect;

                    if (aspects == ImageAspectFlags.None)
                        goto SkipSwapchainDepthClear;

                    attachments[count++] = new ClearAttachment
                    {
                        AspectMask = aspects,
                        ClearValue = new ClearValue
                        {
                            DepthStencil = new ClearDepthStencilValue
                            {
                                Depth = op.Depth,
                                Stencil = op.Stencil
                            }
                        }
                    };
                }

            SkipSwapchainDepthClear:

                if (count > 0)
                    Api!.CmdClearAttachments(commandBuffer, count, attachments, 1, rectPtr);

                return;
            }

            var vkFrameBuffer = clearTargetFrameBuffer;
            if (vkFrameBuffer is null)
                return;

            uint maxAttachments = Math.Max(vkFrameBuffer.AttachmentCount + 1u, 2u);
            ClearAttachment* fboAttachments = stackalloc ClearAttachment[(int)maxAttachments];
            uint fboCount = vkFrameBuffer.WriteClearAttachments(
                fboAttachments,
                clearColor,
                op.ClearDepth,
                op.ClearStencil,
                op.Color,
                op.Depth,
                op.Stencil);
            string targetName = op.Target.Name ?? "<unnamed>";
            if (DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsWatchedFrameBufferName(targetName))
            {
                Debug.VulkanEvery(
                    $"DeferredLighting.CmdClearAttachments.{targetName}",
                    TimeSpan.FromSeconds(1),
                    "[DeferredLightingDiag][CmdClearAttachments] target='{0}' count={1} color={2} depth={3} stencil={4} rect=({5},{6},{7},{8})",
                    targetName,
                    fboCount,
                    clearColor,
                    op.ClearDepth,
                    op.ClearStencil,
                    clearArea.Offset.X,
                    clearArea.Offset.Y,
                    clearArea.Extent.Width,
                    clearArea.Extent.Height);
            }

            if (fboCount > 0)
                Api!.CmdClearAttachments(commandBuffer, fboCount, fboAttachments, 1, rectPtr);
        }

        private static uint ResolveClearRectLayerCount(
            XRFrameBuffer? target,
            VkFrameBuffer? clearTargetFrameBuffer,
            uint activeRenderLayerCount,
            uint activeRenderViewMask)
        {
            if (target is null)
                return 1u;

            if (activeRenderViewMask != 0u || clearTargetFrameBuffer?.MultiviewViewMask != 0u)
                return 1u;

            if (activeRenderLayerCount > 1u && IsStereoCompatibleClearTarget(target, clearTargetFrameBuffer))
                return 1u;

            if (activeRenderLayerCount > 1u && RuntimeEngine.Rendering.State.IsStereoPass)
                return 1u;

            if (activeRenderLayerCount > 0u)
                return Math.Max(activeRenderLayerCount, 1u);

            return Math.Max(clearTargetFrameBuffer?.FramebufferLayers ?? 1u, 1u);
        }

        private static bool IsStereoCompatibleClearTarget(XRFrameBuffer target, VkFrameBuffer? clearTargetFrameBuffer)
        {
            var targets = target.Targets;
            if (targets is null || targets.Length == 0)
                return false;

            uint framebufferLayers = clearTargetFrameBuffer?.FramebufferLayers ?? 0u;
            for (int i = 0; i < targets.Length; i++)
            {
                var (attachmentTarget, _, _, layerIndex) = targets[i];
                if (layerIndex >= 0)
                    continue;

                if (attachmentTarget is XRTexture texture &&
                    VkFrameBuffer.IsStereoCompatibleTextureArrayAttachment(texture, framebufferLayers))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatFboAttachmentSignature(FrameBufferAttachmentSignature[] signatures)
        {
            if (signatures.Length == 0)
                return "<none>";

            StringBuilder builder = new();
            for (int i = 0; i < signatures.Length; i++)
            {
                if (i > 0)
                    builder.Append("; ");

                FrameBufferAttachmentSignature signature = signatures[i];
                builder
                    .Append(i)
                    .Append(":role=").Append(signature.Role)
                    .Append("/color=").Append(signature.ColorIndex)
                    .Append("/format=").Append(signature.Format)
                    .Append("/samples=").Append(signature.Samples)
                    .Append("/aspect=").Append(signature.AspectMask)
                    .Append("/load=").Append(signature.LoadOp)
                    .Append("/store=").Append(signature.StoreOp)
                    .Append("/stencilLoad=").Append(signature.StencilLoadOp)
                    .Append("/stencilStore=").Append(signature.StencilStoreOp)
                    .Append("/initial=").Append(signature.InitialLayout)
                    .Append("/ref=").Append(signature.ReferenceLayout)
                    .Append("/final=").Append(signature.FinalLayout);
            }

            return builder.ToString();
        }

        private static Rect2D ClampRectToExtent(Rect2D rect, Extent2D extent)
        {
            int extentWidth = (int)Math.Max(extent.Width, 1u);
            int extentHeight = (int)Math.Max(extent.Height, 1u);

            int x = Math.Clamp(rect.Offset.X, 0, extentWidth);
            int y = Math.Clamp(rect.Offset.Y, 0, extentHeight);

            int maxWidth = Math.Max(extentWidth - x, 0);
            int maxHeight = Math.Max(extentHeight - y, 0);

            int width = Math.Clamp((int)rect.Extent.Width, 0, maxWidth);
            int height = Math.Clamp((int)rect.Extent.Height, 0, maxHeight);

            return new Rect2D
            {
                Offset = new Offset2D(x, y),
                Extent = new Extent2D((uint)width, (uint)height)
            };
        }

        private static Rect2D ClampRectToRenderArea(Rect2D rect, Rect2D renderArea)
        {
            int renderLeft = renderArea.Offset.X;
            int renderTop = renderArea.Offset.Y;
            int renderRight = AddExtentClamped(renderArea.Offset.X, renderArea.Extent.Width);
            int renderBottom = AddExtentClamped(renderArea.Offset.Y, renderArea.Extent.Height);

            int rectLeft = rect.Offset.X;
            int rectTop = rect.Offset.Y;
            int rectRight = AddExtentClamped(rect.Offset.X, rect.Extent.Width);
            int rectBottom = AddExtentClamped(rect.Offset.Y, rect.Extent.Height);

            int left = Math.Max(rectLeft, renderLeft);
            int top = Math.Max(rectTop, renderTop);
            int right = Math.Min(rectRight, renderRight);
            int bottom = Math.Min(rectBottom, renderBottom);

            if (right <= left || bottom <= top)
            {
                return new Rect2D
                {
                    Offset = new Offset2D(left, top),
                    Extent = new Extent2D(0, 0)
                };
            }

            return new Rect2D
            {
                Offset = new Offset2D(left, top),
                Extent = new Extent2D((uint)(right - left), (uint)(bottom - top))
            };
        }

        private static int AddExtentClamped(int offset, uint extent)
        {
            long value = (long)offset + extent;
            if (value > int.MaxValue)
                return int.MaxValue;
            if (value < int.MinValue)
                return int.MinValue;
            return (int)value;
        }

        internal void RecordPublishFramebufferForSamplingOp(CommandBuffer commandBuffer, PublishFramebufferForSamplingOp op)
        {
            XRFrameBuffer fbo = op.FrameBuffer;
            if (GetOrCreateAPIRenderObject(fbo, generateNow: true) is not VkFrameBuffer vkFbo)
                return;

            vkFbo.EnsureCurrent();
            if (vkFbo.AttachmentCount == 0)
                return;

            int maxLayerSpan = Math.Max((int)vkFbo.FramebufferLayers, 1);
            ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[checked((int)vkFbo.AttachmentCount * maxLayerSpan)];
            uint barrierCount = 0;
            PipelineStageFlags srcStages = 0;
            PipelineStageFlags dstStages = 0;

            for (int attachmentIndex = 0; attachmentIndex < (int)vkFbo.AttachmentCount; attachmentIndex++)
            {
                if (!vkFbo.TryGetAttachmentTarget(
                    attachmentIndex,
                    out IFrameBufferAttachement? target,
                    out EFrameBufferAttachment attachment,
                    out int mipLevel,
                    out int layerIndex) ||
                    !IsColorAttachment(attachment))
                {
                    continue;
                }

                const ImageAspectFlags requestedAspect = ImageAspectFlags.ColorBit;
                if (!TryResolveAttachmentImage(target, mipLevel, layerIndex, requestedAspect, out BlitImageInfo info) ||
                    info.Image.Handle == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.PublishFboForSampling.Unresolved.{fbo.GetHashCode()}.{attachmentIndex}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping publish-for-sampling for '{0}' attachment {1}: image handle could not be resolved.",
                        fbo.Name ?? "<unnamed>",
                        attachmentIndex);
                    continue;
                }

                Image transitionImage = info.Image;
                uint transitionMipLevel = info.MipLevel;
                uint imageBaseLayer;
                uint transitionLayerCount;
                ImageAspectFlags aspectMask = NormalizeBarrierAspectMask(info.Format, requestedAspect);

                if (vkFbo.TryGetAttachmentView(attachmentIndex, out ImageView attachmentView) &&
                    TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo viewInfo) &&
                    viewInfo.Image.Handle != 0)
                {
                    transitionImage = viewInfo.Image;
                    transitionMipLevel = viewInfo.SubresourceRange.BaseMipLevel;
                    imageBaseLayer = viewInfo.SubresourceRange.BaseArrayLayer;
                    transitionLayerCount = Math.Max(viewInfo.SubresourceRange.LayerCount, 1u);

                    ImageAspectFlags viewAspect = NormalizeBarrierAspectMask(info.Format, viewInfo.SubresourceRange.AspectMask);
                    if (viewAspect != ImageAspectFlags.None)
                        aspectMask = viewAspect;
                }
                else
                {
                    ResolveFboAttachmentImageLayerSpan(
                        vkFbo,
                        layerIndex,
                        in info,
                        out imageBaseLayer,
                        out transitionLayerCount);
                }

                ImageLayout targetLayout = ResolvePublishedSampledLayout(info.DescriptorSource, aspectMask);
                uint layerCount = Math.Max(transitionLayerCount, 1u);
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint imageLayer = imageBaseLayer + layerOffset;
                    ImageSubresourceRange transitionRange = new()
                    {
                        AspectMask = aspectMask,
                        BaseMipLevel = transitionMipLevel,
                        LevelCount = 1,
                        BaseArrayLayer = imageLayer,
                        LayerCount = 1
                    };

                    ImageLayout oldLayout = TryGetRecordedImageAccessState(
                        commandBuffer,
                        transitionImage,
                        transitionRange,
                        out VulkanImageAccessState recordedState)
                            ? recordedState.Layout
                            : ImageLayout.Undefined;
                    if (oldLayout == ImageLayout.Undefined)
                        oldLayout = ImageLayout.ColorAttachmentOptimal;

                    if (oldLayout == targetLayout)
                        continue;

                    PipelineStageFlags srcStage = ResolvePublishedSampledSourceStage(oldLayout);
                    PipelineStageFlags dstStage = ResolvePublishedSampledDestinationStage(targetLayout);
                    ImageMemoryBarrier barrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = ResolvePublishedSampledSourceAccess(oldLayout),
                        DstAccessMask = ResolvePublishedSampledDestinationAccess(targetLayout),
                        OldLayout = oldLayout,
                        NewLayout = targetLayout,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = transitionImage,
                        SubresourceRange = transitionRange
                    };

                    barriers[barrierCount++] = barrier;
                    srcStages |= srcStage;
                    dstStages |= dstStage;
                }
            }

            if (barrierCount == 0)
                return;

            CmdPipelineBarrierTracked(
                commandBuffer,
                NormalizePipelineStages(srcStages),
                NormalizePipelineStages(dstStages),
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                barrierCount,
                barriers);
        }

        private static ImageLayout ResolvePublishedSampledLayout(IVkImageDescriptorSource? source, ImageAspectFlags aspectMask)
        {
            if (source is not null &&
                (source.DescriptorUsage & ImageUsageFlags.StorageBit) != 0 &&
                (source.DescriptorUsage & ImageUsageFlags.SampledBit) != 0)
            {
                return ImageLayout.General;
            }

            return IsDepthOrStencilAspect(aspectMask)
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;
        }

        private static PipelineStageFlags ResolvePublishedSampledSourceStage(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => PipelineStageFlags.TopOfPipeBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
                ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthAttachmentOptimal =>
                    PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
                ImageLayout.ShaderReadOnlyOptimal or ImageLayout.DepthStencilReadOnlyOptimal =>
                    PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                ImageLayout.General => PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                ImageLayout.PresentSrcKhr => PipelineStageFlags.BottomOfPipeBit,
                _ => PipelineStageFlags.AllCommandsBit
            };

        private static PipelineStageFlags ResolvePublishedSampledDestinationStage(ImageLayout layout)
            => layout == ImageLayout.General
                ? PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit
                : PipelineStageFlags.FragmentShaderBit;

        private static AccessFlags ResolvePublishedSampledSourceAccess(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => 0,
                ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthAttachmentOptimal =>
                    AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.ShaderReadBit,
                ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
                ImageLayout.General => AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit
            };

        private static AccessFlags ResolvePublishedSampledDestinationAccess(ImageLayout layout)
            => layout == ImageLayout.General
                ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
                : AccessFlags.ShaderReadBit;


    }
}
