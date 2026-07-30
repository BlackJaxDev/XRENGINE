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
        private void EmitPendingMemoryBarriers(CommandBuffer commandBuffer)
        {
            var pendingMask = ActiveState.PendingMemoryBarrierMask;
            if (pendingMask == EMemoryBarrierMask.None)
                return;

            EmitMemoryBarrierMask(commandBuffer, pendingMask);
            ActiveState.ClearPendingMemoryBarrierMask();
        }

        /// <summary>
        /// Emits a <c>vkCmdPipelineBarrier</c> for the given <see cref="EMemoryBarrierMask"/>.
        /// Used both for global pending barriers and per-pass barriers.
        /// </summary>
        private void EmitMemoryBarrierMask(CommandBuffer commandBuffer, EMemoryBarrierMask mask)
        {
            if (mask == EMemoryBarrierMask.None)
                return;

            ResolveBarrierScopes(mask, out PipelineStageFlags srcStages, out PipelineStageFlags dstStages, out AccessFlags srcAccess, out AccessFlags dstAccess);

            MemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                srcStages,
                dstStages,
                DependencyFlags.None,
                1,
                &memoryBarrier,
                0,
                null,
                0,
                null);
        }

        private void ResolveBarrierScopes(
            EMemoryBarrierMask mask,
            out PipelineStageFlags srcStages,
            out PipelineStageFlags dstStages,
            out AccessFlags srcAccess,
            out AccessFlags dstAccess)
        {
            PipelineStageFlags srcStagesLocal = 0;
            PipelineStageFlags dstStagesLocal = 0;
            AccessFlags srcAccessLocal = 0;
            AccessFlags dstAccessLocal = 0;

            MergeBarrierScope((mask & EMemoryBarrierMask.VertexAttribArray) != 0,
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferWriteBit | AccessFlags.VertexAttributeReadBit,
                AccessFlags.VertexAttributeReadBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & EMemoryBarrierMask.ElementArray) != 0,
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferWriteBit | AccessFlags.IndexReadBit,
                AccessFlags.IndexReadBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & EMemoryBarrierMask.Uniform) != 0,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit,
                AccessFlags.UniformReadBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & (EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.TextureUpdate)) != 0,
                PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.TransferWriteBit | AccessFlags.ShaderReadBit,
                AccessFlags.ShaderReadBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & (EMemoryBarrierMask.ShaderGlobalAccess | EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.ShaderStorage)) != 0,
                PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & EMemoryBarrierMask.Command) != 0,
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                PipelineStageFlags.DrawIndirectBit,
                AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
                AccessFlags.IndirectCommandReadBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & (EMemoryBarrierMask.PixelBuffer | EMemoryBarrierMask.BufferUpdate)) != 0,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit,
                AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit | AccessFlags.VertexAttributeReadBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & EMemoryBarrierMask.Framebuffer) != 0,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            if ((mask & EMemoryBarrierMask.TransformFeedback) != 0)
            {
                if (SupportsTransformFeedback)
                {
                    MergeBarrierScope(
                        true,
                        PipelineStageFlags.TransformFeedbackBitExt,
                        PipelineStageFlags.TransformFeedbackBitExt |
                            PipelineStageFlags.VertexInputBit |
                            PipelineStageFlags.VertexShaderBit |
                            PipelineStageFlags.GeometryShaderBit |
                            PipelineStageFlags.ComputeShaderBit |
                            PipelineStageFlags.TransferBit |
                            PipelineStageFlags.DrawIndirectBit,
                        AccessFlags.TransformFeedbackWriteBitExt |
                            AccessFlags.TransformFeedbackCounterWriteBitExt,
                        AccessFlags.TransformFeedbackWriteBitExt |
                        AccessFlags.TransformFeedbackCounterReadBitExt |
                            AccessFlags.VertexAttributeReadBit |
                            AccessFlags.ShaderReadBit |
                            AccessFlags.TransferReadBit |
                            AccessFlags.IndirectCommandReadBit,
                        ref srcStagesLocal,
                        ref dstStagesLocal,
                        ref srcAccessLocal,
                        ref dstAccessLocal);
                }
                else
                {
                    MergeBarrierScope(
                        true,
                        PipelineStageFlags.AllCommandsBit,
                        PipelineStageFlags.AllCommandsBit,
                        AccessFlags.MemoryWriteBit,
                        AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                        ref srcStagesLocal,
                        ref dstStagesLocal,
                        ref srcAccessLocal,
                        ref dstAccessLocal);
                }
            }

            MergeBarrierScope((mask & EMemoryBarrierMask.AtomicCounter) != 0,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & EMemoryBarrierMask.ClientMappedBuffer) != 0,
                PipelineStageFlags.HostBit,
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.HostWriteBit,
                AccessFlags.TransferReadBit | AccessFlags.VertexAttributeReadBit | AccessFlags.UniformReadBit | AccessFlags.ShaderReadBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            MergeBarrierScope((mask & EMemoryBarrierMask.GpuReadback) != 0,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.HostBit,
                AccessFlags.TransferWriteBit,
                AccessFlags.HostReadBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            // Query buffers: AllCommandsBit is justified per Vulkan spec because
            // queries can be written by any pipeline stage.
            MergeBarrierScope((mask & EMemoryBarrierMask.QueryBuffer) != 0,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit,
                AccessFlags.MemoryWriteBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                ref srcStagesLocal,
                ref dstStagesLocal,
                ref srcAccessLocal,
                ref dstAccessLocal);

            if (srcStagesLocal == 0)
                srcStagesLocal = PipelineStageFlags.AllCommandsBit;
            if (dstStagesLocal == 0)
                dstStagesLocal = PipelineStageFlags.AllCommandsBit;
            if (srcAccessLocal == 0)
                srcAccessLocal = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
            if (dstAccessLocal == 0)
                dstAccessLocal = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;

            srcStages = srcStagesLocal;
            dstStages = dstStagesLocal;
            srcAccess = srcAccessLocal;
            dstAccess = dstAccessLocal;
        }

        private static void MergeBarrierScope(
            bool condition,
            PipelineStageFlags srcStage,
            PipelineStageFlags dstStage,
            AccessFlags srcAccess,
            AccessFlags dstAccess,
            ref PipelineStageFlags mergedSrcStages,
            ref PipelineStageFlags mergedDstStages,
            ref AccessFlags mergedSrcAccess,
            ref AccessFlags mergedDstAccess)
        {
            if (!condition)
                return;

            mergedSrcStages |= srcStage;
            mergedDstStages |= dstStage;
            mergedSrcAccess |= srcAccess;
            mergedDstAccess |= dstAccess;
        }

        /// <summary>
        /// After ending a render pass for an FBO target, update the tracked layout
        /// on each physical image group backing the FBO's attachments. The render
        /// pass transitions each attachment from <c>initialLayout</c> through the
        /// subpass layout to <c>finalLayout</c> at <c>CmdEndRenderPass</c>.
        /// We must track the <b>finalLayout</b>, not the subpass layout.
        /// </summary>
        private void TransitionFboAttachmentsForDynamicRendering(
            CommandBuffer commandBuffer,
            XRFrameBuffer fbo,
            FrameBufferAttachmentSignature[] signatures,
            bool beginRendering)
        {
            if (signatures.Length == 0)
                return;

            VkFrameBuffer? vkFbo = GenericToAPI<VkFrameBuffer>(fbo);
            if (vkFbo is null || vkFbo.AttachmentCount == 0)
                return;

            int attachmentCapacity = Math.Min((int)vkFbo.AttachmentCount, signatures.Length);
            if (attachmentCapacity <= 0)
                return;

            int maxLayerSpan = Math.Max((int)vkFbo.FramebufferLayers, 1);
            ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[checked(attachmentCapacity * maxLayerSpan)];
            uint barrierCount = 0;
            PipelineStageFlags srcStages = 0;
            PipelineStageFlags dstStages = 0;

            for (int i = 0; i < attachmentCapacity; i++)
            {
                FrameBufferAttachmentSignature signature = signatures[i];
                if (signature.Role == AttachmentRole.Unused)
                    continue;
                ImageLayout requestedOldLayout = NormalizeFboAttachmentLayout(
                    signature,
                    beginRendering ? signature.InitialLayout : signature.ReferenceLayout);
                ImageLayout newLayout = NormalizeFboAttachmentLayout(
                    signature,
                    beginRendering ? signature.ReferenceLayout : signature.FinalLayout);
                if (newLayout == ImageLayout.Undefined)
                    continue;

                if (!vkFbo.TryGetAttachmentTarget(
                    i,
                    out IFrameBufferAttachement? target,
                    out _,
                    out int mipLevel,
                    out int layerIndex))
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.DynamicRendering.FboTransition.NoTarget.{fbo.GetHashCode()}.{i}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping dynamic-rendering FBO transition for '{0}' attachment {1}: ordered attachment target metadata was unavailable.",
                        fbo.Name ?? "<unnamed>",
                        i);
                    continue;
                }

                ImageAspectFlags aspectMask = NormalizeBarrierAspectMask(signature.Format, signature.AspectMask);
                if (!TryResolveAttachmentImage(target, mipLevel, layerIndex, aspectMask, out BlitImageInfo info) ||
                    info.Image.Handle == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.DynamicRendering.FboTransition.Unresolved.{fbo.GetHashCode()}.{i}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping dynamic-rendering FBO transition for '{0}' attachment {1}: image handle could not be resolved.",
                        fbo.Name ?? "<unnamed>",
                        i);
                    continue;
                }

                Image transitionImage = info.Image;
                uint transitionMipLevel = info.MipLevel;
                uint imageBaseLayer;
                uint transitionLayerCount;
                if (vkFbo.TryGetAttachmentView(i, out ImageView attachmentView) &&
                    TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo viewInfo) &&
                    viewInfo.Image.Handle != 0)
                {
                    transitionImage = viewInfo.Image;
                    transitionMipLevel = viewInfo.SubresourceRange.BaseMipLevel;
                    imageBaseLayer = viewInfo.SubresourceRange.BaseArrayLayer;
                    transitionLayerCount = Math.Max(viewInfo.SubresourceRange.LayerCount, 1u);

                    ImageAspectFlags viewAspect = NormalizeBarrierAspectMask(signature.Format, viewInfo.SubresourceRange.AspectMask);
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

                ResolveFboAttachmentTrackedLayerSpan(
                    vkFbo,
                    layerIndex,
                    out uint trackedBaseLayer,
                    out uint trackedLayerCount);

                uint layerCount = Math.Max(transitionLayerCount, 1u);
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint imageLayer = imageBaseLayer + layerOffset;
                    int trackedLayer = checked((int)(trackedBaseLayer + Math.Min(layerOffset, Math.Max(trackedLayerCount, 1u) - 1u)));
                    ImageSubresourceRange transitionRange = new()
                    {
                        AspectMask = aspectMask,
                        BaseMipLevel = transitionMipLevel,
                        LevelCount = 1,
                        BaseArrayLayer = imageLayer,
                        LayerCount = 1
                    };
                    ImageLayout oldLayout;
                    if (TryGetRecordedImageAccessState(
                        commandBuffer,
                        transitionImage,
                        transitionRange,
                        out VulkanImageAccessState recordedState))
                    {
                        oldLayout = NormalizeFboAttachmentLayout(signature, recordedState.Layout);
                    }
                    else
                    {
                        oldLayout = NormalizeFboAttachmentLayout(signature, requestedOldLayout);
                    }
                    bool sameLayout = oldLayout == newLayout;
                    // A render-pass attachment can remain in the same layout while
                    // its producer changes.  Dynamic rendering has no implicit
                    // external-subpass dependency, so retain this memory barrier on
                    // scope exit for a later sampled read of the attachment.

                    bool oldLayoutIsRenderAttachment = !beginRendering;
                    bool newLayoutIsRenderAttachment = beginRendering;
                    PipelineStageFlags srcStage = sameLayout
                        ? PipelineStageFlags.AllCommandsBit
                        : oldLayout == ImageLayout.Undefined
                        ? PipelineStageFlags.TopOfPipeBit
                        : ResolveFboAttachmentStage(oldLayout, signature, oldLayoutIsRenderAttachment);
                    PipelineStageFlags dstStage = ResolveFboAttachmentStage(newLayout, signature, newLayoutIsRenderAttachment);

                    ImageMemoryBarrier barrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = sameLayout
                            ? AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit
                            : oldLayout == ImageLayout.Undefined
                            ? 0
                            : ResolveFboAttachmentAccess(oldLayout, signature, oldLayoutIsRenderAttachment),
                        DstAccessMask = ResolveFboAttachmentAccess(newLayout, signature, newLayoutIsRenderAttachment),
                        OldLayout = oldLayout,
                        NewLayout = newLayout,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = transitionImage,
                        SubresourceRange = transitionRange
                    };

                    bool traceDynamicFboTransition =
                        CommandRecordingDiagnosticsEnabled ||
                        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
                        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw ||
                        BloomVulkanDiagnosticsEnabled && IsBloomDiagnosticName(fbo.Name);
                    if (traceDynamicFboTransition)
                    {
                        string targetName = target switch
                        {
                            XRTexture texture => texture.Name ?? texture.GetDescribingName(),
                            XRRenderBuffer renderBuffer => renderBuffer.Name ?? renderBuffer.GetType().Name,
                            _ => target.GetType().Name
                        } ?? "<unnamed>";

                        Debug.VulkanEvery(
                            $"Vulkan.DynamicRendering.FboTransition.{fbo.Name}.{i}.{beginRendering}.{info.MipLevel}.{imageLayer}.{oldLayout}.{newLayout}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Dynamic FBO transition fbo='{0}' begin={1} attachment={2} target='{3}' viewMask=0x{4:X} imageLayer={5}/{6} trackedLayer={7}/{8} old={9} new={10} aspect={11} image=0x{12:X}",
                            fbo.Name ?? "<unnamed>",
                            beginRendering,
                            i,
                            targetName,
                            vkFbo.MultiviewViewMask,
                            imageLayer,
                            transitionLayerCount,
                            trackedLayer,
                            trackedLayerCount,
                            oldLayout,
                            newLayout,
                            aspectMask,
                            transitionImage.Handle);
                    }

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

        private static ImageLayout NormalizeFboAttachmentLayout(FrameBufferAttachmentSignature signature, ImageLayout layout)
        {
            if (layout == ImageLayout.Undefined || layout == ImageLayout.General ||
                layout == ImageLayout.TransferSrcOptimal || layout == ImageLayout.TransferDstOptimal ||
                layout == ImageLayout.PresentSrcKhr)
            {
                return layout;
            }

            bool isDepthStencil = signature.Role is AttachmentRole.Depth or AttachmentRole.Stencil or AttachmentRole.DepthStencil ||
                IsDepthOrStencilFormat(signature.Format) ||
                (signature.AspectMask & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0;
            if (!isDepthStencil)
            {
                return layout switch
                {
                    ImageLayout.DepthStencilAttachmentOptimal => ImageLayout.ColorAttachmentOptimal,
                    ImageLayout.DepthStencilReadOnlyOptimal or ImageLayout.DepthReadOnlyOptimal or ImageLayout.StencilReadOnlyOptimal =>
                        ImageLayout.ShaderReadOnlyOptimal,
                    _ => layout
                };
            }

            return layout switch
            {
                ImageLayout.ColorAttachmentOptimal => ImageLayout.DepthStencilAttachmentOptimal,
                ImageLayout.ShaderReadOnlyOptimal => ImageLayout.DepthStencilReadOnlyOptimal,
                _ => layout
            };
        }

        private static FrameBufferAttachmentSignature[] CreateLegacyRenderPassSignature(
            FrameBufferAttachmentSignature[] signatures)
        {
            FrameBufferAttachmentSignature[] result = (FrameBufferAttachmentSignature[])signatures.Clone();
            for (int i = 0; i < result.Length; i++)
            {
                FrameBufferAttachmentSignature signature = result[i];
                if (signature.Role == AttachmentRole.Unused || signature.ReferenceLayout == ImageLayout.Undefined)
                    continue;

                result[i] = new FrameBufferAttachmentSignature(
                    signature.Format,
                    signature.Samples,
                    signature.AspectMask,
                    signature.Role,
                    signature.ColorIndex,
                    signature.LoadOp,
                    signature.StoreOp,
                    signature.StencilLoadOp,
                    signature.StencilStoreOp,
                    signature.ReferenceLayout,
                    signature.FinalLayout,
                    signature.ReferenceLayout);
            }

            return result;
        }

        private static PipelineStageFlags ResolveFboAttachmentStage(
            ImageLayout layout,
            FrameBufferAttachmentSignature signature,
            bool asRenderAttachment)
        {
            if (layout == ImageLayout.ShaderReadOnlyOptimal)
                return PipelineStageFlags.FragmentShaderBit;

            if (layout is ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal)
                return PipelineStageFlags.TransferBit;

            if (layout == ImageLayout.ColorAttachmentOptimal ||
                (asRenderAttachment && IsColorLikeAttachmentRole(signature.Role)))
                return PipelineStageFlags.ColorAttachmentOutputBit;

            if (layout is ImageLayout.DepthStencilAttachmentOptimal ||
                (asRenderAttachment && signature.Role is AttachmentRole.Depth or AttachmentRole.Stencil or AttachmentRole.DepthStencil))
            {
                return PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
            }

            if (layout is ImageLayout.DepthStencilReadOnlyOptimal
                    or ImageLayout.DepthReadOnlyOptimal
                    or ImageLayout.StencilReadOnlyOptimal)
            {
                PipelineStageFlags stages = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
                if (!asRenderAttachment)
                    stages |= PipelineStageFlags.FragmentShaderBit;
                return stages;
            }

            return PipelineStageFlags.AllGraphicsBit;
        }

        private static AccessFlags ResolveFboAttachmentAccess(
            ImageLayout layout,
            FrameBufferAttachmentSignature signature,
            bool asRenderAttachment)
        {
            if (layout == ImageLayout.ShaderReadOnlyOptimal)
                return AccessFlags.ShaderReadBit;

            if (layout == ImageLayout.TransferSrcOptimal)
                return AccessFlags.TransferReadBit;

            if (layout == ImageLayout.TransferDstOptimal)
                return AccessFlags.TransferWriteBit;

            if (layout == ImageLayout.ColorAttachmentOptimal ||
                (asRenderAttachment && IsColorLikeAttachmentRole(signature.Role)))
                return AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;

            if (layout is ImageLayout.DepthStencilReadOnlyOptimal
                    or ImageLayout.DepthReadOnlyOptimal
                    or ImageLayout.StencilReadOnlyOptimal)
            {
                AccessFlags access = AccessFlags.DepthStencilAttachmentReadBit;
                if (asRenderAttachment)
                    access |= AccessFlags.DepthStencilAttachmentWriteBit;
                else
                    access |= AccessFlags.ShaderReadBit;
                return access;
            }

            if (layout == ImageLayout.DepthStencilAttachmentOptimal ||
                (asRenderAttachment && signature.Role is AttachmentRole.Depth or AttachmentRole.Stencil or AttachmentRole.DepthStencil))
            {
                return AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
            }

            return AccessFlags.MemoryReadBit;
        }

        private static void ResolveFboAttachmentImageLayerSpan(
            VkFrameBuffer vkFbo,
            int layerIndex,
            in BlitImageInfo info,
            out uint baseLayer,
            out uint layerCount)
        {
            baseLayer = info.BaseArrayLayer;
            layerCount = Math.Max(info.LayerCount, 1u);

            if (TryResolveViewMaskLayerSpan(vkFbo.MultiviewViewMask, out uint viewBaseLayer, out uint viewLayerCount))
            {
                baseLayer += viewBaseLayer;
                layerCount = Math.Max(viewLayerCount, 1u);
                return;
            }

            if (layerIndex < 0)
            {
                baseLayer = info.BaseArrayLayer;
                layerCount = Math.Max(vkFbo.FramebufferLayers, layerCount);
            }
        }

        private static void ResolveFboAttachmentTrackedLayerSpan(
            VkFrameBuffer vkFbo,
            int layerIndex,
            out uint baseLayer,
            out uint layerCount)
        {
            if (TryResolveViewMaskLayerSpan(vkFbo.MultiviewViewMask, out baseLayer, out layerCount))
                return;

            if (layerIndex < 0)
            {
                baseLayer = 0u;
                layerCount = Math.Max(vkFbo.FramebufferLayers, 1u);
                return;
            }

            baseLayer = (uint)Math.Max(layerIndex, 0);
            layerCount = 1u;
        }

        private static bool TryResolveViewMaskLayerSpan(uint viewMask, out uint baseLayer, out uint layerCount)
        {
            baseLayer = 0u;
            layerCount = 0u;
            if (viewMask == 0u)
                return false;

            uint first = 32u;
            uint last = 0u;
            for (uint bit = 0u; bit < 32u; bit++)
            {
                if ((viewMask & (1u << (int)bit)) == 0u)
                    continue;

                first = Math.Min(first, bit);
                last = Math.Max(last, bit);
            }

            if (first >= 32u)
                return false;

            baseLayer = first;
            layerCount = last - first + 1u;
            return true;
        }

        /// <summary>
        /// Queries the current tracked layout of each attachment backing the given FBO.
        /// Returns an array suitable for <see cref="VkFrameBuffer.ResolveRenderPassForPass"/>
        /// that reflects any barrier-planner or blit transitions since the last render pass.
        /// </summary>
        private ImageLayout[]? QueryCurrentAttachmentLayouts(XRFrameBuffer fbo, VkFrameBuffer vkFbo)
        {
            if (vkFbo.AttachmentCount == 0)
                return null;

            int count = (int)vkFbo.AttachmentCount;
            ImageLayout[] layouts = GetFboAttachmentLayoutScratch(fbo, count);

            for (int i = 0; i < count; i++)
            {
                if (!vkFbo.TryGetAttachmentTarget(
                    i,
                    out IFrameBufferAttachement? target,
                    out EFrameBufferAttachment attachment,
                    out int mipLevel,
                    out int layerIndex))
                {
                    layouts[i] = ImageLayout.Undefined;
                    continue;
                }

                layouts[i] = TryGetExactTrackedFboAttachmentLayout(
                    vkFbo,
                    i,
                    target,
                    attachment,
                    mipLevel,
                    layerIndex,
                    out ImageLayout layout)
                    ? layout
                    : ImageLayout.Undefined;
            }

            return layouts;
        }

        private ImageLayout[] GetFboAttachmentLayoutScratch(XRFrameBuffer fbo, int attachmentCount)
        {
            CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;
            if (!recordingScratch.FboAttachmentLayouts.TryGetValue(
                    fbo,
                    out CommandBufferRecordingScratch.FboAttachmentLayoutScratch? scratch))
            {
                scratch = new CommandBufferRecordingScratch.FboAttachmentLayoutScratch();
                recordingScratch.FboAttachmentLayouts.Add(fbo, scratch);
            }

            if (scratch.Layouts.Length != attachmentCount)
                scratch.Layouts = new ImageLayout[attachmentCount];

            recordingScratch.FboLayoutTracking[fbo] = scratch.Layouts;
            return scratch.Layouts;
        }
        private bool TryGetExactTrackedFboAttachmentLayout(
            VkFrameBuffer vkFbo,
            int attachmentIndex,
            IFrameBufferAttachement target,
            EFrameBufferAttachment attachment,
            int mipLevel,
            int layerIndex,
            out ImageLayout layout)
        {
            layout = ImageLayout.Undefined;

            ImageAspectFlags requestedAspect = ResolveFrameBufferAttachmentAspectMask(attachment);
            if (requestedAspect == ImageAspectFlags.None ||
                !TryResolveAttachmentImage(target, mipLevel, layerIndex, requestedAspect, out BlitImageInfo info) ||
                info.Image.Handle == 0)
            {
                return false;
            }

            Image image = info.Image;
            ImageSubresourceRange range = new()
            {
                AspectMask = NormalizeBarrierAspectMask(info.Format, requestedAspect),
                BaseMipLevel = info.MipLevel,
                LevelCount = 1,
                BaseArrayLayer = info.BaseArrayLayer,
                LayerCount = Math.Max(info.LayerCount, 1u)
            };

            if (vkFbo.TryGetAttachmentView(attachmentIndex, out ImageView attachmentView) &&
                TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo viewInfo) &&
                viewInfo.Image.Handle != 0)
            {
                image = viewInfo.Image;
                range = viewInfo.SubresourceRange;
                range.AspectMask = NormalizeBarrierAspectMask(info.Format, range.AspectMask);
                range.LevelCount = Math.Max(range.LevelCount, 1u);
                range.LayerCount = Math.Max(range.LayerCount, 1u);
            }

            return TryGetTrackedImageLayout(image, range, out layout);
        }

        private static ImageAspectFlags ResolveFrameBufferAttachmentAspectMask(EFrameBufferAttachment attachment)
        {
            if (IsColorAttachment(attachment))
                return ImageAspectFlags.ColorBit;

            return attachment switch
            {
                EFrameBufferAttachment.DepthStencilAttachment => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                EFrameBufferAttachment.DepthAttachment => ImageAspectFlags.DepthBit,
                EFrameBufferAttachment.StencilAttachment => ImageAspectFlags.StencilBit,
                _ => ImageAspectFlags.None
            };
        }

        /// <summary>
        /// When the barrier planner has no known passes, emit image memory barriers to
        /// transition any physical-group images still in <see cref="ImageLayout.Undefined"/>
        /// to a usable layout inside the current command buffer. Keeping this transition
        /// in-frame avoids out-of-band one-shot submissions while resource-planner states
        /// switch between desktop and OpenXR targets.
        /// </summary>
        private void EmitInitialImageBarriersForUnknownPass(
            CommandBuffer commandBuffer,
            bool skipDesktopSwapchainImages = false)
        {
            foreach (VulkanPhysicalImageGroup group in ResourceAllocator.EnumeratePhysicalGroups())
            {
                if (!group.IsAllocated || group.Image.Handle == 0)
                    continue;
                if (skipDesktopSwapchainImages && IsDesktopSwapchainImage(group.Image))
                    continue;

                bool isDepth = VulkanResourceAllocator.IsDepthStencilFormat(group.Format);
                ImageLayout targetLayout = ResolveInitialPhysicalGroupLayout(group.Usage, isDepth);

                PipelineStageFlags initDstStage = targetLayout switch
                {
                    ImageLayout.DepthStencilAttachmentOptimal =>
                        PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                    ImageLayout.ColorAttachmentOptimal =>
                        PipelineStageFlags.ColorAttachmentOutputBit,
                    ImageLayout.General =>
                        PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                    ImageLayout.ShaderReadOnlyOptimal =>
                        PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                    ImageLayout.TransferDstOptimal or ImageLayout.TransferSrcOptimal =>
                        PipelineStageFlags.TransferBit,
                    _ => PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                };
                VulkanImageAccessState targetState = ResolveVulkanImageAccessState(
                    targetLayout,
                    isDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit);
                AccessFlags initDstAccess = (AccessFlags)(ulong)targetState.AccessMask;

                if (isDepth)
                {
                    EmitInitialImageAspectBarriers(
                        commandBuffer,
                        group,
                        HasStencilComponent(group.Format)
                            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                            : ImageAspectFlags.DepthBit,
                        targetLayout,
                        initDstStage,
                        initDstAccess);
                }
                else
                {
                    EmitInitialImageAspectBarriers(
                        commandBuffer,
                        group,
                        ImageAspectFlags.ColorBit,
                        targetLayout,
                        initDstStage,
                        initDstAccess);
                }
            }
        }

        private void RecordFboAttachmentAccessState(
            CommandBuffer commandBuffer,
            VkFrameBuffer vkFbo,
            FrameBufferAttachmentSignature[] signatures,
            bool useReferenceLayouts)
        {
            int attachmentCount = Math.Min((int)vkFbo.AttachmentCount, signatures.Length);
            for (int attachmentIndex = 0; attachmentIndex < attachmentCount; attachmentIndex++)
            {
                FrameBufferAttachmentSignature signature = signatures[attachmentIndex];
                if (signature.Role == AttachmentRole.Unused)
                    continue;
                ImageLayout layout = useReferenceLayouts
                    ? signature.ReferenceLayout
                    : signature.FinalLayout;
                if (layout == ImageLayout.Undefined ||
                    !vkFbo.TryGetAttachmentView(attachmentIndex, out ImageView attachmentView) ||
                    !TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo viewInfo) ||
                    viewInfo.Image.Handle == 0)
                {
                    continue;
                }

                ImageSubresourceRange range = viewInfo.SubresourceRange;
                range.AspectMask = NormalizeBarrierAspectMask(signature.Format, range.AspectMask);
                range.LevelCount = Math.Max(range.LevelCount, 1u);
                range.LayerCount = Math.Max(range.LayerCount, 1u);
                // The published access state must describe the same point in
                // time as the published layout. A render-pass final layout can
                // differ from its attachment reference layout (for example,
                // color attachment -> shader read). Pairing FinalLayout with
                // reference-time color-write masks creates a contradictory
                // state and makes a reusable primary reject the next frame.
                ImageLayout accessLayout = layout;
                PipelineStageFlags stageMask = ResolveFboAttachmentStage(
                    accessLayout,
                    signature,
                    asRenderAttachment: true);
                AccessFlags accessMask = ResolveFboAttachmentAccess(
                    accessLayout,
                    signature,
                    asRenderAttachment: true);
                RecordImageAccess(
                    commandBuffer,
                    viewInfo.Image,
                    range,
                    layout,
                    stageMask,
                    accessMask,
                    Vk.QueueFamilyIgnored);
            }
        }

        private void EmitInitialImageAspectBarriers(
            CommandBuffer commandBuffer,
            VulkanPhysicalImageGroup group,
            ImageAspectFlags aspect,
            ImageLayout targetLayout,
            PipelineStageFlags dstStage,
            AccessFlags dstAccess)
        {
            uint mipLevels = Math.Max(1u, group.MipLevels);
            uint layers = Math.Max(1u, group.Template.Layers);
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                uint layer = 0;
                while (layer < layers)
                {
                    ImageSubresourceRange single = new()
                    {
                        AspectMask = aspect,
                        BaseMipLevel = mip,
                        LevelCount = 1,
                        BaseArrayLayer = layer,
                        LayerCount = 1,
                    };
                    if (TryGetRecordedImageAccessState(commandBuffer, group.Image, single, out _))
                    {
                        layer++;
                        continue;
                    }

                    uint firstUnknownLayer = layer++;
                    while (layer < layers)
                    {
                        single.BaseArrayLayer = layer;
                        if (TryGetRecordedImageAccessState(commandBuffer, group.Image, single, out _))
                            break;
                        layer++;
                    }

                    ImageMemoryBarrier barrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        OldLayout = ImageLayout.Undefined,
                        NewLayout = targetLayout,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = group.Image,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = aspect,
                            BaseMipLevel = mip,
                            LevelCount = 1,
                            BaseArrayLayer = firstUnknownLayer,
                            LayerCount = layer - firstUnknownLayer,
                        },
                        SrcAccessMask = AccessFlags.None,
                        DstAccessMask = dstAccess,
                    };

                    CmdPipelineBarrierTracked(
                        commandBuffer,
                        PipelineStageFlags.TopOfPipeBit,
                        dstStage,
                        DependencyFlags.None,
                        0, null, 0, null,
                        1, &barrier);
                }
            }
        }

        private void EmitPlannedImageBarriers(
            CommandBuffer commandBuffer,
            IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier>? plannedBarriers,
            bool skipDesktopSwapchainImages = false)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0)
                return;

            for (int plannedIndex = 0; plannedIndex < plannedBarriers.Count; plannedIndex++)
            {
                VulkanBarrierPlanner.PlannedImageBarrier planned = plannedBarriers[plannedIndex];
                planned.Group.EnsureAllocated(this);
                if (skipDesktopSwapchainImages && IsDesktopSwapchainImage(planned.Group.Image))
                    continue;

                // The barrier planner pre-computes OldLayout from the logical dependency
                // graph, but dynamic rendering, blits, and resource-plan replacement can
                // change the live VkImage layout before the planned edge is emitted. Vulkan
                // validation cares about the live subresource layout, so use the physical
                // group's tracker whenever it has a concrete value.
                ImageLayout effectiveOldLayout = planned.Previous.Layout;
                ImageSubresourceRange range = new()
                {
                    AspectMask = NormalizeBarrierAspectMask(planned.Group.Format, planned.Next.AspectMask),
                    BaseMipLevel = planned.Range.BaseMipLevel,
                    LevelCount = Math.Max(1u, planned.Range.LevelCount),
                    BaseArrayLayer = planned.Range.BaseArrayLayer,
                    LayerCount = Math.Max(1u, planned.Range.LayerCount)
                };
                if (TryGetRecordedImageLayout(
                        commandBuffer,
                        planned.Group.Image,
                        range,
                        out ImageLayout recordedLayout) &&
                    recordedLayout != effectiveOldLayout)
                {
                    if (CommandRecordingDiagnosticsEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Barrier.OldLayout.Reconciled.{planned.ResourceName}.{planned.PassIndex}",
                            TimeSpan.FromSeconds(2),
                            "[Vulkan] Reconciled planned oldLayout for '{0}' pass={1}: planned={2} tracked={3} next={4}.",
                            planned.ResourceName,
                            planned.PassIndex,
                            effectiveOldLayout,
                            recordedLayout,
                            planned.Next.Layout);
                    }
                    effectiveOldLayout = recordedLayout;
                }

                if (BloomVulkanDiagnosticsEnabled && IsBloomDiagnosticName(planned.ResourceName))
                {
                    Debug.VulkanEvery(
                        $"Vulkan.BloomDiag.PlannedBarrier.{planned.ResourceName}.{planned.PassIndex}.{range.BaseMipLevel}.{range.LevelCount}.{range.BaseArrayLayer}.{range.LayerCount}.{effectiveOldLayout}.{planned.Next.Layout}",
                        TimeSpan.FromSeconds(1),
                        "[BloomDiag][Vulkan] planned pass={0} resource='{1}' mip={2}+{3} layer={4}+{5} old={6} new={7} prevStage={8} nextStage={9} prevAccess={10} nextAccess={11} aspect={12} image=0x{13:X}",
                        planned.PassIndex,
                        planned.ResourceName,
                        range.BaseMipLevel,
                        range.LevelCount,
                        range.BaseArrayLayer,
                        range.LayerCount,
                        effectiveOldLayout,
                        planned.Next.Layout,
                        planned.Previous.StageMask,
                        planned.Next.StageMask,
                        planned.Previous.AccessMask,
                        planned.Next.AccessMask,
                        range.AspectMask,
                        planned.Group.Image.Handle);
                }

                ImageMemoryBarrier barrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = FilterAccessFlagsForStages(planned.Previous.AccessMask, planned.Previous.StageMask),
                    DstAccessMask = FilterAccessFlagsForStages(planned.Next.AccessMask, planned.Next.StageMask),
                    OldLayout = effectiveOldLayout,
                    NewLayout = planned.Next.Layout,
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Image = planned.Group.Image,
                    SubresourceRange = range
                };

                PipelineStageFlags srcStages = NormalizePipelineStages(planned.Previous.StageMask);
                PipelineStageFlags dstStages = NormalizePipelineStages(planned.Next.StageMask);

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    srcStages,
                    dstStages,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);

            }
        }

        private bool IsDesktopSwapchainImage(Image image)
        {
            if (image.Handle == 0 || swapChainImages is null)
                return false;

            for (int i = 0; i < swapChainImages.Length; i++)
                if (swapChainImages[i].Handle == image.Handle)
                    return true;

            return false;
        }

        private void EmitPlannedBufferBarriers(CommandBuffer commandBuffer, IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier>? plannedBarriers)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0)
                return;

            for (int plannedIndex = 0; plannedIndex < plannedBarriers.Count; plannedIndex++)
            {
                VulkanBarrierPlanner.PlannedBufferBarrier planned = plannedBarriers[plannedIndex];
                if (!TryResolveTrackedBuffer(planned.ResourceName, out Silk.NET.Vulkan.Buffer buffer, out ulong size) || buffer.Handle == 0)
                    continue;

                BufferMemoryBarrier barrier = new()
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = FilterAccessFlagsForStages(planned.Previous.AccessMask, planned.Previous.StageMask),
                    DstAccessMask = FilterAccessFlagsForStages(planned.Next.AccessMask, planned.Next.StageMask),
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Buffer = buffer,
                    Offset = 0,
                    Size = size > 0 ? size : Vk.WholeSize
                };

                PipelineStageFlags srcStages = NormalizePipelineStages(planned.Previous.StageMask);
                PipelineStageFlags dstStages = NormalizePipelineStages(planned.Next.StageMask);

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    srcStages,
                    dstStages,
                    DependencyFlags.None,
                    0,
                    null,
                    1,
                    &barrier,
                    0,
                    null);
            }
        }

        private int GetOrAssignPrimaryMeshDrawUniformSlot(
            int opIndex,
            int[] meshDrawUniformSlotsByOpIndex,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshDrawSlotsByRendererFamily,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshFrameDataFamilyBases,
            int frameDataImageIndex,
            VkMeshRenderer renderer,
            in FrameOpContext context,
            in PendingMeshDraw draw)
        {
            int cachedSlot = meshDrawUniformSlotsByOpIndex[opIndex];
            if (cachedSlot >= 0)
                return cachedSlot;

            int drawSlot = GetFrameWideMeshDrawUniformSlot(
                meshDrawSlotsByRendererFamily,
                meshFrameDataFamilyBases,
                renderer,
                frameDataImageIndex,
                EVulkanMeshFrameDataStreamKind.Primary,
                context,
                draw);
            meshDrawUniformSlotsByOpIndex[opIndex] = drawSlot;
            return drawSlot;
        }

        private void TransitionFrameOpDescriptorSnapshotsForSampling(
            CommandBuffer commandBuffer,
            FrameOp[] ops,
            int startIndex,
            int passIndex,
            int schedulingIdentity,
            int[] meshDrawUniformSlotsByOpIndex,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshDrawSlotsByRendererFamily,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshFrameDataFamilyBases,
            int frameDataImageIndex)
        {
            for (int i = startIndex; i < ops.Length; i++)
            {
                FrameOp candidate = ops[i];
                int candidatePassIndex = candidate.PassIndex == int.MinValue
                    ? passIndex
                    : EnsureValidPassIndex(candidate.PassIndex, candidate.GetType().Name, candidate.Context.PassMetadata);
                if (candidatePassIndex != passIndex || candidate.Context.SchedulingIdentity != schedulingIdentity)
                    break;

                bool transitionedPublishedDescriptors = false;
                if (candidate is MeshDrawOp meshDraw)
                {
                    int drawUniformSlot = GetOrAssignPrimaryMeshDrawUniformSlot(
                        i,
                        meshDrawUniformSlotsByOpIndex,
                        meshDrawSlotsByRendererFamily,
                        meshFrameDataFamilyBases,
                        frameDataImageIndex,
                        meshDraw.Draw.Renderer,
                        meshDraw.Context,
                        meshDraw.Draw);
                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        meshDraw.Context.PipelineInstance);
                    using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(meshDraw.Context);
                    transitionedPublishedDescriptors = meshDraw.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(
                        commandBuffer,
                        meshDraw.Draw,
                        drawUniformSlot,
                        frameDataImageIndex,
                        meshDraw.Target);
                }
                else if (candidate is IndirectDrawOp indirectDraw)
                {
                    int drawUniformSlot = GetOrAssignPrimaryMeshDrawUniformSlot(
                        i,
                        meshDrawUniformSlotsByOpIndex,
                        meshDrawSlotsByRendererFamily,
                        meshFrameDataFamilyBases,
                        frameDataImageIndex,
                        indirectDraw.MeshRenderer,
                        indirectDraw.Context,
                        indirectDraw.Draw);
                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        indirectDraw.Context.PipelineInstance);
                    using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(indirectDraw.Context);
                    transitionedPublishedDescriptors = indirectDraw.MeshRenderer.TryTransitionPreparedDescriptorImagesForSampling(
                        commandBuffer,
                        indirectDraw.Draw,
                        drawUniformSlot,
                        frameDataImageIndex,
                        indirectDraw.Target);
                }

                // A published descriptor snapshot names the physical image view that
                // this draw will bind. Only fall back to the logical texture snapshot
                // when that exact allocation is unavailable (for example heap mode).
                if (transitionedPublishedDescriptors)
                    continue;

                ComputeDispatchSnapshot? snapshot = candidate switch
                {
                    MeshDrawOp logicalMeshDraw => logicalMeshDraw.Draw.ProgramBindingSnapshot,
                    IndirectDrawOp logicalIndirectDraw => logicalIndirectDraw.Draw.ProgramBindingSnapshot,
                    ComputeDispatchOp compute => compute.Snapshot,
                    ComputeDispatchIndirectOp computeIndirect => computeIndirect.Snapshot,
                    _ => null,
                };
                if (snapshot is null)
                    continue;

                foreach (XRTexture texture in snapshot.Samplers.Values)
                    TransitionDescriptorTextureForSampling(commandBuffer, texture, candidate.Target);
                foreach (XRTexture texture in snapshot.SamplersByName.Values)
                    TransitionDescriptorTextureForSampling(commandBuffer, texture, candidate.Target);
            }
        }

        internal bool TransitionPublishedDescriptorSetImagesForSampling(
            CommandBuffer commandBuffer,
            DescriptorSet descriptorSet,
            XRFrameBuffer? target)
        {
            if (descriptorSet.Handle == 0 ||
                !_resourceLifetimeTracker.PublishedDescriptorSets.TryGetValue(
                    descriptorSet.Handle,
                    out VulkanPublishedDescriptorSetSnapshot? snapshot))
            {
                return false;
            }

            for (int i = 0; i < snapshot.ImageReferences.Length; i++)
            {
                VulkanPublishedDescriptorImageReference published = snapshot.ImageReferences[i];
                if (snapshot.HasReflection && Array.IndexOf(snapshot.ReflectedImageBindings, published.Binding) < 0)
                    continue;
                if (published.Reference.Type is not (
                    DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.InputAttachment))
                    continue;

                TransitionDescriptorImageForSampling(commandBuffer, published.Reference.View, published.Reference.Layout, target);
            }

            return true;
        }

        private void TransitionDescriptorTextureForSampling(
            CommandBuffer commandBuffer,
            XRTexture texture,
            XRFrameBuffer? target)
        {
            if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source ||
                source.DescriptorView.Handle == 0 ||
                !TryGetDescriptorHeapImageViewCreateInfo(source.DescriptorView, out ImageViewCreateInfo viewInfo) ||
                viewInfo.Image.Handle == 0)
            {
                return;
            }

            ImageLayout targetLayout = ResolveDescriptorImageLayout(source, DescriptorType.CombinedImageSampler);
            TransitionDescriptorImageForSampling(commandBuffer, source.DescriptorView, targetLayout, target);
        }

        private void TransitionDescriptorImageForSampling(
            CommandBuffer commandBuffer,
            ImageView imageView,
            ImageLayout targetLayout,
            XRFrameBuffer? target)
        {
            if (imageView.Handle == 0 ||
                targetLayout == ImageLayout.Undefined ||
                !TryGetDescriptorHeapImageViewCreateInfo(imageView, out ImageViewCreateInfo viewInfo) ||
                viewInfo.Image.Handle == 0)
            {
                return;
            }

            ImageSubresourceRange range = viewInfo.SubresourceRange;
            range.AspectMask = NormalizeBarrierAspectMask(viewInfo.Format, range.AspectMask);
            range.LevelCount = Math.Max(range.LevelCount, 1u);
            range.LayerCount = Math.Max(range.LayerCount, 1u);
            if (IsImageRangeAttachedToFrameBuffer(target, viewInfo.Image, range))
                return;

            VulkanImageAccessState priorState;
            if (!TryGetRecordedImageAccessState(
                    commandBuffer,
                    viewInfo.Image,
                    range,
                    out priorState))
            {
                ulong resourceGeneration = GetCurrentVulkanResourceGeneration(
                    ObjectType.Image,
                    viewInfo.Image.Handle);
                if (resourceGeneration == 0)
                    return;

                // Internally-created VkImages begin in UNDEFINED. Their first descriptor
                // use must publish that transition on the primary command buffer before a
                // reusable secondary records the shader-read entry requirement.
                priorState = VulkanImageAccessState.Undefined with
                {
                    ResourceGeneration = resourceGeneration,
                };
            }

            if (priorState.Layout == targetLayout)
                return;

            VulkanImageAccessState nextState = ResolveVulkanImageAccessState(targetLayout, range.AspectMask);
            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = (AccessFlags)(ulong)priorState.AccessMask,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = priorState.Layout,
                NewLayout = targetLayout,
                SrcQueueFamilyIndex = priorState.QueueFamilyIndex,
                DstQueueFamilyIndex = priorState.QueueFamilyIndex,
                Image = viewInfo.Image,
                SubresourceRange = range,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                (PipelineStageFlags)(ulong)priorState.StageMask,
                (PipelineStageFlags)(ulong)nextState.StageMask,
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                1,
                &barrier,
                nameof(TransitionFrameOpDescriptorSnapshotsForSampling));
        }

        private bool IsImageRangeAttachedToFrameBuffer(
            XRFrameBuffer? target,
            Image image,
            ImageSubresourceRange range)
        {
            if (target is null || GenericToAPI<VkFrameBuffer>(target) is not { } vkFbo)
                return false;

            for (int i = 0; i < vkFbo.AttachmentCount; i++)
            {
                if (!vkFbo.TryGetAttachmentView(i, out ImageView attachmentView) ||
                    !TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo attachmentInfo) ||
                    attachmentInfo.Image.Handle != image.Handle)
                {
                    continue;
                }

                ImageSubresourceRange attachmentRange = attachmentInfo.SubresourceRange;
                bool aspectOverlap = (attachmentRange.AspectMask & range.AspectMask) != 0;
                bool mipOverlap = attachmentRange.BaseMipLevel < range.BaseMipLevel + Math.Max(range.LevelCount, 1u) &&
                    range.BaseMipLevel < attachmentRange.BaseMipLevel + Math.Max(attachmentRange.LevelCount, 1u);
                bool layerOverlap = attachmentRange.BaseArrayLayer < range.BaseArrayLayer + Math.Max(range.LayerCount, 1u) &&
                    range.BaseArrayLayer < attachmentRange.BaseArrayLayer + Math.Max(attachmentRange.LayerCount, 1u);
                if (aspectOverlap && mipOverlap && layerOverlap)
                    return true;
            }

            return false;
        }

        private static bool PrepareQueryFrameOpsForCommandBufferReuse(
            CommandBuffer commandBuffer,
            FrameOp[] ops)
        {
            for (int index = 0; index < ops.Length; index++)
            {
                if (ops[index] is QueryOp queryOp &&
                    queryOp.Operation is (
                        ERenderQueryOperation.Reset or
                        ERenderQueryOperation.Begin or
                        ERenderQueryOperation.WriteTimestamp or
                        ERenderQueryOperation.WriteProperties) &&
                    !queryOp.Query.PrepareForCommandBufferReuse(commandBuffer))
                {
                    return false;
                }
            }

            return true;
        }

        /// Appends dynamic rendering local-read pNext structs when a pass explicitly
        /// opts into framebuffer-local attachment reads.
        private bool TryAppendDynamicRenderingLocalReadPNext(
            in DynamicRenderingLocalReadPlan localRead,
            uint colorAttachmentCount,
            ref void* pNext,
            RenderingAttachmentLocationInfo* attachmentLocationInfo,
            RenderingInputAttachmentIndexInfo* inputAttachmentIndexInfo,
            uint* colorAttachmentLocations,
            uint* colorInputAttachmentIndices,
            uint* depthInputAttachmentIndex,
            uint* stencilInputAttachmentIndex)
        {
            if (!SupportsDynamicRenderingLocalRead || !localRead.Enabled)
                return false;

            bool hasAttachmentLocations = localRead.ColorAttachmentLocations.Length > 0;
            bool hasColorInputIndices = localRead.ColorInputAttachmentIndices.Length > 0;
            bool hasInputIndices =
                hasColorInputIndices ||
                localRead.DepthInputAttachmentIndex.HasValue ||
                localRead.StencilInputAttachmentIndex.HasValue;

            if (!hasAttachmentLocations && !hasInputIndices)
                return false;

            if ((hasAttachmentLocations && (uint)localRead.ColorAttachmentLocations.Length != colorAttachmentCount) ||
                (hasColorInputIndices && (uint)localRead.ColorInputAttachmentIndices.Length != colorAttachmentCount))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.DynamicRendering.LocalRead.InvalidPlan",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan] Dynamic rendering local-read plan ignored because color counts do not match the active rendering scope (attachments={0}, locations={1}, inputIndices={2}).",
                    colorAttachmentCount,
                    localRead.ColorAttachmentLocations.Length,
                    localRead.ColorInputAttachmentIndices.Length);
                return false;
            }

            if ((hasAttachmentLocations && (attachmentLocationInfo is null || colorAttachmentLocations is null)) ||
                (hasInputIndices && (inputAttachmentIndexInfo is null || (hasColorInputIndices && colorInputAttachmentIndices is null))) ||
                (localRead.DepthInputAttachmentIndex.HasValue && depthInputAttachmentIndex is null) ||
                (localRead.StencilInputAttachmentIndex.HasValue && stencilInputAttachmentIndex is null))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.DynamicRendering.LocalRead.MissingScratch",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan] Dynamic rendering local-read plan ignored because scratch storage was not provided for the pNext chain.");
                return false;
            }

            void* next = pNext;

            if (hasAttachmentLocations)
            {
                for (int i = 0; i < localRead.ColorAttachmentLocations.Length; i++)
                    colorAttachmentLocations[i] = localRead.ColorAttachmentLocations[i];

                *attachmentLocationInfo = new RenderingAttachmentLocationInfo
                {
                    SType = StructureType.RenderingAttachmentLocationInfo,
                    PNext = next,
                    ColorAttachmentCount = colorAttachmentCount,
                    PColorAttachmentLocations = colorAttachmentLocations,
                };
                next = attachmentLocationInfo;
            }

            if (hasInputIndices)
            {
                uint* colorInputPtr = null;
                uint colorInputCount = 0;
                if (hasColorInputIndices)
                {
                    for (int i = 0; i < localRead.ColorInputAttachmentIndices.Length; i++)
                        colorInputAttachmentIndices[i] = localRead.ColorInputAttachmentIndices[i];

                    colorInputPtr = colorInputAttachmentIndices;
                    colorInputCount = colorAttachmentCount;
                }

                uint* depthInputPtr = null;
                if (localRead.DepthInputAttachmentIndex.HasValue)
                {
                    *depthInputAttachmentIndex = localRead.DepthInputAttachmentIndex.Value;
                    depthInputPtr = depthInputAttachmentIndex;
                }

                uint* stencilInputPtr = null;
                if (localRead.StencilInputAttachmentIndex.HasValue)
                {
                    *stencilInputAttachmentIndex = localRead.StencilInputAttachmentIndex.Value;
                    stencilInputPtr = stencilInputAttachmentIndex;
                }

                *inputAttachmentIndexInfo = new RenderingInputAttachmentIndexInfo
                {
                    SType = StructureType.RenderingInputAttachmentIndexInfo,
                    PNext = next,
                    ColorAttachmentCount = colorInputCount,
                    PColorAttachmentInputIndices = colorInputPtr,
                    PDepthInputAttachmentIndex = depthInputPtr,
                    PStencilInputAttachmentIndex = stencilInputPtr,
                };
                next = inputAttachmentIndexInfo;
            }

            pNext = next;
            return true;
        }

        /// Pipeline stages must not be zero; fall back to AllCommandsBit as safety net.
        /// The planner should produce non-zero masks; this guards against edge cases.
        private static PipelineStageFlags NormalizePipelineStages(PipelineStageFlags stageMask)
            => stageMask == 0 ? PipelineStageFlags.AllCommandsBit : stageMask;

        private static AccessFlags FilterAccessFlagsForStages(AccessFlags accessMask, PipelineStageFlags stageMask)
        {
            if (accessMask == 0)
                return 0;

            if ((stageMask & (PipelineStageFlags.AllCommandsBit | PipelineStageFlags.AllGraphicsBit)) != 0)
                return accessMask;

            AccessFlags allowed = 0;

            if ((stageMask & PipelineStageFlags.TransferBit) != 0)
                allowed |= AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit;

            if ((stageMask & PipelineStageFlags.DrawIndirectBit) != 0)
                allowed |= AccessFlags.IndirectCommandReadBit;

            if ((stageMask & PipelineStageFlags.VertexInputBit) != 0)
                allowed |= AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit;

            if ((stageMask & (PipelineStageFlags.VertexShaderBit |
                              PipelineStageFlags.TessellationControlShaderBit |
                              PipelineStageFlags.TessellationEvaluationShaderBit |
                              PipelineStageFlags.GeometryShaderBit |
                              PipelineStageFlags.FragmentShaderBit |
                              PipelineStageFlags.ComputeShaderBit |
                              PipelineStageFlags.TaskShaderBitNV |
                              PipelineStageFlags.MeshShaderBitNV)) != 0)
            {
                allowed |= AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.UniformReadBit;
            }

            if ((stageMask & PipelineStageFlags.ColorAttachmentOutputBit) != 0)
                allowed |= AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;

            if ((stageMask & (PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit)) != 0)
                allowed |= AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;

            if ((stageMask & PipelineStageFlags.HostBit) != 0)
                allowed |= AccessFlags.HostReadBit | AccessFlags.HostWriteBit;

            if (allowed == 0)
                return accessMask;

            return accessMask & allowed;
        }

    }
}
