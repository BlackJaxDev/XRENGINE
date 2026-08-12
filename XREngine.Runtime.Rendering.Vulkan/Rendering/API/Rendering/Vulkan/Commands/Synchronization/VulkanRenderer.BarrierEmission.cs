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
using XREngine.Rendering.RenderGraph;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {
        private void EmitPendingMemoryBarriers(CommandBuffer commandBuffer)
        {
            var pendingMask = StateTracker.PendingMemoryBarrierMask;
            if (pendingMask == EMemoryBarrierMask.None)
                return;

            EmitMemoryBarrierMask(commandBuffer, pendingMask);
            StateTracker.ClearPendingMemoryBarrierMask();
        }

        /// <summary>
        /// Emits a <c>vkCmdPipelineBarrier</c> for the given <see cref="EMemoryBarrierMask"/>.
        /// Used both for global pending barriers and per-pass barriers.
        /// </summary>
        internal unsafe void EmitMemoryBarrierMask(CommandBuffer commandBuffer, EMemoryBarrierMask mask)
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
                if (ResourceRuntime.BackendObjectContext?.SupportsTransformFeedback == true)
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
        private unsafe void TransitionFboAttachmentsForDynamicRendering(
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
            using VulkanNativeScratchReservation<ImageMemoryBarrier> barrierReservation =
                Synchronization._synchronizationThreadWorkspace.Current.ImageMemoryBarrierScratch.Reserve(
                    checked(attachmentCapacity * maxLayerSpan));
            Span<ImageMemoryBarrier> barriers = barrierReservation.Span;
            uint barrierCount = 0;
            PipelineStageFlags srcStages = 0;
            PipelineStageFlags dstStages = 0;

            for (int i = 0; i < attachmentCapacity; i++)
            {
                FrameBufferAttachmentSignature signature = signatures[i];
                if (signature.Role == AttachmentRole.Unused)
                    continue;
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

                ImageAspectFlags aspectMask = VulkanCommandRuntime.NormalizeBarrierAspectMask(signature.Format, signature.AspectMask);
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

                    ImageAspectFlags viewAspect = VulkanCommandRuntime.NormalizeBarrierAspectMask(signature.Format, viewInfo.SubresourceRange.AspectMask);
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
                        out VulkanImageAccessState recordedState,
                        // Entry snapshots describe what a cached command buffer
                        // expects at execution. They do not prove a freshly
                        // allocated image has ever left UNDEFINED. Attachment
                        // opening must consult only states established by this
                        // recording or a completed submission.
                        includeEntryState: false))
                    {
                        oldLayout = NormalizeFboAttachmentLayout(signature, recordedState.Layout);
                    }
                    else
                    {
                        // Dynamic rendering has no implicit initial-layout
                        // transition. If neither this command buffer nor the
                        // submitted-state tracker knows the allocation, it is a
                        // newly created internal image and its native layout is
                        // UNDEFINED regardless of the render-pass-style initial
                        // layout carried by the attachment signature.
                        oldLayout = ImageLayout.Undefined;
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
                            "[Vulkan] Dynamic FBO transition fbo='{0}' begin={1} attachment={2} target='{3}' viewMask=0x{4:X} mip={5} imageLayer={6}/{7} trackedLayer={8}/{9} old={10} new={11} aspect={12} image=0x{13:X}",
                            fbo.Name ?? "<unnamed>",
                            beginRendering,
                            i,
                            targetName,
                            vkFbo.MultiviewViewMask,
                            transitionMipLevel,
                            imageLayer,
                            transitionLayerCount,
                            trackedLayer,
                            trackedLayerCount,
                            oldLayout,
                            newLayout,
                            aspectMask,
                            transitionImage.Handle);
                    }

                    barriers[checked((int)barrierCount++)] = barrier;
                    srcStages |= srcStage;
                    dstStages |= dstStage;
                }
            }

            if (barrierCount == 0)
                return;

            fixed (ImageMemoryBarrier* nativeBarriers = barriers)
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
                    nativeBarriers);
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
                VulkanCommandRuntime.IsDepthOrStencilFormat(signature.Format) ||
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
        private ImageLayout[]? QueryCurrentAttachmentLayouts(
            XRFrameBuffer fbo,
            VkFrameBuffer vkFbo,
            CommandBuffer commandBuffer = default)
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
                    commandBuffer,
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
            CommandBuffer commandBuffer,
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
                AspectMask = VulkanCommandRuntime.NormalizeBarrierAspectMask(info.Format, requestedAspect),
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
                range.AspectMask = VulkanCommandRuntime.NormalizeBarrierAspectMask(info.Format, range.AspectMask);
                range.LevelCount = Math.Max(range.LevelCount, 1u);
                range.LayerCount = Math.Max(range.LayerCount, 1u);
            }

            // The primary may already have written or transitioned this image in
            // the frame currently being recorded.  Consult its local access state
            // before the last submitted state so aliased attachments (notably the
            // G-buffer/forward shared depth image) reopen with LOAD instead of
            // being treated as first-use UNDEFINED.
            if (commandBuffer.Handle != 0 &&
                TryGetRecordedImageLayout(commandBuffer, image, in range, out layout))
            {
                return true;
            }

            return TryGetTrackedImageLayout(image, in range, out layout);
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
                range.AspectMask = VulkanCommandRuntime.NormalizeBarrierAspectMask(signature.Format, range.AspectMask);
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

        private unsafe void EmitPlannedImageBarriers(
            CommandBuffer commandBuffer,
            ReadOnlySpan<VulkanFrozenImageBarrier> plannedBarriers,
            Image excludedImage = default)
        {
            if (plannedBarriers.IsEmpty)
                return;

            // A frozen plan is already grouped by pass.  Preserve that shape at
            // the API boundary: one contiguous native array and one command per
            // pass, rather than a native call and scratch lease per resource.
            using VulkanNativeScratchReservation<ImageMemoryBarrier2> barrierReservation =
                Synchronization._synchronizationThreadWorkspace.Current.ImageMemoryBarrier2Scratch.Reserve(plannedBarriers.Length);
            Span<ImageMemoryBarrier2> barriers = barrierReservation.Span;
            uint barrierCount = 0;
            for (int plannedIndex = 0; plannedIndex < plannedBarriers.Length; plannedIndex++)
            {
                VulkanFrozenImageBarrier planned = plannedBarriers[plannedIndex];
                if (excludedImage.Handle != 0 && planned.NativeImage.Handle == excludedImage.Handle)
                    continue;

                // Physical resource allocation is a producer responsibility. The
                // prepared primary input freezes an already-allocated resource
                // graph; command recording must never reach back into a resource
                // provider or mutate planner-owned state.
                if (planned.NativeImage.Handle == 0)
                    throw new InvalidOperationException(
                        $"Frozen barrier resource id={planned.ResourceId.Value} became unavailable after prepared-input validation.");

                // The barrier planner pre-computes OldLayout from the logical dependency
                // graph, but dynamic rendering, blits, and resource-plan replacement can
                // change the live VkImage layout before the planned edge is emitted. Vulkan
                // validation cares about the live subresource layout, so use the physical
                // group's tracker whenever it has a concrete value.
                ImageLayout effectiveOldLayout = planned.Previous.Layout;
                ImageSubresourceRange range = new()
                {
                    AspectMask = VulkanCommandRuntime.NormalizeBarrierAspectMask(planned.NativeFormat, planned.Next.AspectMask),
                    BaseMipLevel = planned.Range.BaseMipLevel,
                    LevelCount = Math.Max(1u, planned.Range.LevelCount),
                    BaseArrayLayer = planned.Range.BaseArrayLayer,
                    LayerCount = Math.Max(1u, planned.Range.LayerCount)
                };
                if (TryGetRecordedImageAccessState(
                        commandBuffer,
                        planned.NativeImage,
                        in range,
                        out VulkanImageAccessState recordedState,
                        // A reusable command buffer's entry state is an execution
                        // precondition for its previous resource allocation. It
                        // cannot establish the native layout of a replacement
                        // image. Planned barriers must start from state recorded
                        // in this generation or published by a submission.
                        includeEntryState: false,
                        includeUndefinedState: true) &&
                    recordedState.Layout != effectiveOldLayout)
                {
                    ImageLayout recordedLayout = recordedState.Layout;
                    if (CommandRecordingDiagnosticsEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Barrier.OldLayout.Reconciled.{planned.ResourceId.Value}.{planned.PassIndex}",
                            TimeSpan.FromSeconds(2),
                            "[Vulkan] Reconciled planned oldLayout for '{0}' pass={1}: planned={2} tracked={3} next={4}.",
                            planned.ResourceId.Value,
                            planned.PassIndex,
                            effectiveOldLayout,
                            recordedLayout,
                            planned.Next.Layout);
                    }
                    effectiveOldLayout = recordedLayout;
                }

                if (BloomVulkanDiagnosticsEnabled && planned.IsBloomDiagnostic)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.BloomDiag.PlannedBarrier.{planned.ResourceId.Value}.{planned.PassIndex}.{range.BaseMipLevel}.{range.LevelCount}.{range.BaseArrayLayer}.{range.LayerCount}.{effectiveOldLayout}.{planned.Next.Layout}",
                        TimeSpan.FromSeconds(1),
                        "[BloomDiag][Vulkan] planned pass={0} resource='{1}' mip={2}+{3} layer={4}+{5} old={6} new={7} prevStage={8} nextStage={9} prevAccess={10} nextAccess={11} aspect={12} image=0x{13:X}",
                        planned.PassIndex,
                        planned.ResourceId.Value,
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
                        planned.NativeImage.Handle);
                }

                barriers[(int)barrierCount] = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcStageMask = (PipelineStageFlags2)NormalizePipelineStages(planned.Previous.StageMask),
                    DstStageMask = (PipelineStageFlags2)NormalizePipelineStages(planned.Next.StageMask),
                    SrcAccessMask = (AccessFlags2)FilterAccessFlagsForStages(planned.Previous.AccessMask, planned.Previous.StageMask),
                    DstAccessMask = (AccessFlags2)FilterAccessFlagsForStages(planned.Next.AccessMask, planned.Next.StageMask),
                    OldLayout = effectiveOldLayout,
                    NewLayout = planned.Next.Layout,
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Image = planned.NativeImage,
                    SubresourceRange = range
                };
                barrierCount++;

            }

            if (barrierCount == 0)
                return;

            fixed (ImageMemoryBarrier2* nativeBarriers = barriers)
            {
                DependencyInfo dependencyInfo = new()
                {
                    SType = StructureType.DependencyInfo,
                    ImageMemoryBarrierCount = barrierCount,
                    PImageMemoryBarriers = nativeBarriers,
                };
                CmdPipelineBarrier2Tracked(commandBuffer, in dependencyInfo);
            }
        }

        private unsafe void EmitPlannedBufferBarriers(CommandBuffer commandBuffer, ReadOnlySpan<VulkanFrozenBufferBarrier> plannedBarriers)
        {
            if (plannedBarriers.IsEmpty)
                return;

            using VulkanNativeScratchReservation<BufferMemoryBarrier2> barrierReservation =
                Synchronization._synchronizationThreadWorkspace.Current.BufferMemoryBarrier2Scratch.Reserve(plannedBarriers.Length);
            Span<BufferMemoryBarrier2> barriers = barrierReservation.Span;
            for (int plannedIndex = 0; plannedIndex < plannedBarriers.Length; plannedIndex++)
            {
                VulkanFrozenBufferBarrier planned = plannedBarriers[plannedIndex];
                if (planned.NativeBuffer.Handle == 0)
                    throw new InvalidOperationException(
                        $"Frozen buffer barrier resource id={planned.ResourceId.Value} has no native binding.");

                barriers[plannedIndex] = new BufferMemoryBarrier2
                {
                    SType = StructureType.BufferMemoryBarrier2,
                    SrcStageMask = (PipelineStageFlags2)NormalizePipelineStages(planned.Previous.StageMask),
                    DstStageMask = (PipelineStageFlags2)NormalizePipelineStages(planned.Next.StageMask),
                    SrcAccessMask = (AccessFlags2)FilterAccessFlagsForStages(planned.Previous.AccessMask, planned.Previous.StageMask),
                    DstAccessMask = (AccessFlags2)FilterAccessFlagsForStages(planned.Next.AccessMask, planned.Next.StageMask),
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Buffer = planned.NativeBuffer,
                    Offset = planned.NativeOffset,
                    Size = planned.NativeSize > 0 ? planned.NativeSize : Vk.WholeSize
                };

            }

            fixed (BufferMemoryBarrier2* nativeBarriers = barriers)
            {
                DependencyInfo dependencyInfo = new()
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = (uint)plannedBarriers.Length,
                    PBufferMemoryBarriers = nativeBarriers,
                };
                CmdPipelineBarrier2Tracked(commandBuffer, in dependencyInfo);
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
            FrameOperationSequence ops,
            int startIndex,
            int passIndex,
            int schedulingIdentity,
            int[] meshDrawUniformSlotsByOpIndex,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshDrawSlotsByRendererFamily,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshFrameDataFamilyBases,
            int frameDataImageIndex,
            CommandChainKey[]? scheduledCommandChainKeysByOpIndex,
            Dictionary<CommandChainKey, CommandChain>? scheduledCommandChainCache)
        {
            for (int i = startIndex; i < ops.Length; i++)
            {
                ref readonly FrameOperationHeader header = ref ops.GetHeader(i);
                ref readonly FrameOpContext context = ref ops.GetContext(i);
                XRFrameBuffer? target = ops.GetTarget(i);
                int candidatePassIndex = header.PassIndex == int.MinValue
                    ? passIndex
                    : VulkanCommandRuntime.EnsureValidPassIndex(header.PassIndex, header.OpCode.ToString(), context.PassMetadata);
                if (candidatePassIndex != passIndex || context.SchedulingIdentity != schedulingIdentity)
                    break;

                bool transitionedPublishedDescriptors = false;
                if (header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw)
                {
                    ref readonly MeshDrawPayload meshDraw = ref ops.GetMeshDraw(i);
                    // A scheduled mesh secondary publishes its exact, deduplicated
                    // descriptor-image entry requirements immediately before the
                    // primary opens its render scope and executes the buffers.
                    // Scanning the logical draw here repeats
                    // pipeline/planner/slot work for every mesh in an all-reused
                    // chain. If scheduled execution later falls back inline, the
                    // mesh op re-establishes its descriptor transition before it
                    // opens the inline render scope.
                    if (IsDescriptorTransitionOwnedByScheduledMeshChain(
                            i,
                            scheduledCommandChainKeysByOpIndex,
                            scheduledCommandChainCache))
                    {
                        continue;
                    }

                    int drawUniformSlot = GetOrAssignPrimaryMeshDrawUniformSlot(
                        i,
                        meshDrawUniformSlotsByOpIndex,
                        meshDrawSlotsByRendererFamily,
                        meshFrameDataFamilyBases,
                        frameDataImageIndex,
                        meshDraw.Draw.Renderer,
                        context,
                        meshDraw.Draw);
                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        context.PipelineInstance);
                    transitionedPublishedDescriptors = meshDraw.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(
                        commandBuffer,
                        meshDraw.Draw,
                        drawUniformSlot,
                        frameDataImageIndex,
                        target,
                        candidatePassIndex,
                        context.PassMetadata);
                }
                else if (header.OpCode == EVulkanPrimaryPlanNodeKind.IndirectDraw)
                {
                    ref readonly IndirectDrawPayload indirectDraw = ref ops.GetIndirectDraw(i);
                    int drawUniformSlot = GetOrAssignPrimaryMeshDrawUniformSlot(
                        i,
                        meshDrawUniformSlotsByOpIndex,
                        meshDrawSlotsByRendererFamily,
                        meshFrameDataFamilyBases,
                        frameDataImageIndex,
                        indirectDraw.MeshRenderer,
                        context,
                        indirectDraw.Draw);
                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        context.PipelineInstance);
                    transitionedPublishedDescriptors = indirectDraw.MeshRenderer.TryTransitionPreparedDescriptorImagesForSampling(
                        commandBuffer,
                        indirectDraw.Draw,
                        drawUniformSlot,
                        frameDataImageIndex,
                        target,
                        candidatePassIndex,
                        context.PassMetadata);
                }

                // A published descriptor snapshot names the physical image view that
                // this draw will bind. Only fall back to the logical texture snapshot
                // when that exact allocation is unavailable (for example heap mode).
                if (transitionedPublishedDescriptors)
                    continue;

                ComputeDispatchSnapshot? snapshot = header.OpCode switch
                {
                    EVulkanPrimaryPlanNodeKind.MeshDraw => ops.GetMeshDraw(i).Draw.ProgramBindingSnapshot,
                    EVulkanPrimaryPlanNodeKind.IndirectDraw => ops.GetIndirectDraw(i).Draw.ProgramBindingSnapshot,
                    EVulkanPrimaryPlanNodeKind.ComputeDispatch => ops.GetComputeDispatch(i).Snapshot,
                    EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect => ops.GetComputeDispatchIndirect(i).Snapshot,
                    _ => null,
                };
                if (snapshot is null)
                    continue;

                foreach (XRTexture texture in snapshot.Samplers.Values)
                    TransitionDescriptorTextureForSampling(
                        commandBuffer,
                        texture,
                        target,
                        candidatePassIndex,
                        context.PassMetadata);
                foreach (XRTexture texture in snapshot.SamplersByName.Values)
                    TransitionDescriptorTextureForSampling(
                        commandBuffer,
                        texture,
                        target,
                        candidatePassIndex,
                        context.PassMetadata);
            }
        }

        private bool IsDescriptorTransitionOwnedByScheduledMeshChain(
            int opIndex,
            CommandChainKey[]? keysByOpIndex,
            Dictionary<CommandChainKey, CommandChain>? commandChainCache)
        {
            if (keysByOpIndex is null ||
                commandChainCache is null ||
                (uint)opIndex >= (uint)keysByOpIndex.Length)
            {
                return false;
            }

            CommandChainKey key = keysByOpIndex[opIndex];
            if (key.ChainOrdinal < 0 ||
                !commandChainCache.TryGetValue(key, out CommandChain? chain))
            {
                return false;
            }

            return chain.ScheduledPacket &&
                   chain.SourceStartIndex >= 0 &&
                   chain.SourceCount > 0 &&
                   opIndex >= chain.SourceStartIndex &&
                   opIndex < chain.SourceStartIndex + chain.SourceCount &&
                   HasCurrentSecondaryDescriptorPayloadRequirements(
                       chain.SecondaryCommandBuffer);
        }

        internal bool TransitionPublishedDescriptorSetImagesForSampling(
            CommandBuffer commandBuffer,
            DescriptorSet descriptorSet,
            XRFrameBuffer? target,
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        {
            if (descriptorSet.Handle == 0 ||
                !ResourceRuntime.Lifetime.Tracker.PublishedDescriptorSets.TryGetValue(
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

                TransitionDescriptorImageForSampling(
                    commandBuffer,
                    published.Reference.View,
                    published.Reference.Layout,
                    target,
                    passIndex,
                    passMetadata);
            }

            return true;
        }

        /// <summary>
        /// Resolves descriptor payload generations and sampled subresources on
        /// the render thread. Workers receive only this numeric snapshot, so a
        /// secondary encoder never needs to consult a mesh draw's managed
        /// target/context sidecar.
        /// </summary>
        private bool TryPrepareSecondaryDescriptorImageRequirements(
            VulkanPreparedFrameRecording preparedFrame,
            ReadOnlySpan<VulkanPreparedDescriptorSetBinding> descriptorBindings,
            XRFrameBuffer? target,
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata,
            out VulkanPreparedStreamRange payloadRange,
            out VulkanPreparedStreamRange requirementRange,
            out string failureReason)
        {
            int payloadStart = preparedFrame.DescriptorImagePayloadCount;
            int requirementStart = preparedFrame.DescriptorImageRequirementCount;
            failureReason = string.Empty;

            lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            {
                VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
                for (int descriptorIndex = 0; descriptorIndex < descriptorBindings.Length; descriptorIndex++)
                {
                    DescriptorSet descriptorSet = descriptorBindings[descriptorIndex].DescriptorSet;
                    if (descriptorSet.Handle == 0 ||
                        !tracker.PublishedDescriptorSets.TryGetValue(descriptorSet.Handle, out VulkanPublishedDescriptorSetSnapshot? snapshot) ||
                        snapshot.ImagePayloadGeneration == 0)
                    {
                        failureReason = $"descriptor set 0x{descriptorSet.Handle:X} has no published image payload";
                        payloadRange = default;
                        requirementRange = default;
                        return false;
                    }

                    preparedFrame.AddDescriptorImagePayload(new VulkanPreparedDescriptorImagePayload(
                        descriptorSet.Handle,
                        snapshot.ImagePayloadGeneration));
                    for (int imageIndex = 0; imageIndex < snapshot.ImageReferences.Length; imageIndex++)
                    {
                        VulkanPublishedDescriptorImageReference published = snapshot.ImageReferences[imageIndex];
                        VulkanDescriptorImageReference imageReference = published.Reference;
                        if ((snapshot.HasReflection && Array.IndexOf(snapshot.ReflectedImageBindings, published.Binding) < 0) ||
                            imageReference.Type is not (DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.InputAttachment))
                        {
                            continue;
                        }

                        if (imageReference.Layout == ImageLayout.Undefined ||
                            !TryGetDescriptorHeapImageViewCreateInfo(imageReference.View, out ImageViewCreateInfo viewInfo) ||
                            viewInfo.Image.Handle == 0)
                        {
                            failureReason = $"binding {published.Binding}[{published.Element}] has no complete image-view publication";
                            payloadRange = default;
                            requirementRange = default;
                            return false;
                        }

                        ImageSubresourceRange range = viewInfo.SubresourceRange;
                        range.AspectMask = VulkanCommandRuntime.NormalizeBarrierAspectMask(viewInfo.Format, range.AspectMask);
                        range.LevelCount = Math.Max(range.LevelCount, 1u);
                        range.LayerCount = Math.Max(range.LayerCount, 1u);
                        if (IsImageRangeAttachedToFrameBuffer(target, viewInfo.Image, range, imageReference.Layout, passIndex, passMetadata))
                            continue;

                        ulong resourceGeneration = ResourceRuntime.GetPublishedGeneration(ObjectType.Image, viewInfo.Image.Handle);
                        if (resourceGeneration == 0)
                        {
                            failureReason = $"binding {published.Binding}[{published.Element}] image 0x{viewInfo.Image.Handle:X} has no published lifetime generation";
                            payloadRange = default;
                            requirementRange = default;
                            return false;
                        }

                        for (uint mipOffset = 0; mipOffset < range.LevelCount; mipOffset++)
                        for (uint layerOffset = 0; layerOffset < range.LayerCount; layerOffset++)
                        {
                            uint mipLevel = range.BaseMipLevel + mipOffset;
                            uint arrayLayer = range.BaseArrayLayer + layerOffset;
                            AppendPreparedDescriptorImageAspectRequirements(
                                preparedFrame, viewInfo.Image.Handle, resourceGeneration, mipLevel, arrayLayer,
                                range.AspectMask, imageReference.Layout);
                        }
                    }
                }
            }

            payloadRange = new VulkanPreparedStreamRange(payloadStart, preparedFrame.DescriptorImagePayloadCount - payloadStart);
            requirementRange = new VulkanPreparedStreamRange(requirementStart, preparedFrame.DescriptorImageRequirementCount - requirementStart);
            failureReason = "Ready";
            return true;
        }

        private static void AppendPreparedDescriptorImageAspectRequirements(
            VulkanPreparedFrameRecording preparedFrame,
            ulong imageHandle,
            ulong resourceGeneration,
            uint mipLevel,
            uint arrayLayer,
            ImageAspectFlags aspects,
            ImageLayout layout)
        {
            if ((aspects & ImageAspectFlags.ColorBit) != 0)
                preparedFrame.AddDescriptorImageRequirement(new VulkanPreparedDescriptorImageRequirement(imageHandle, resourceGeneration, mipLevel, arrayLayer, ImageAspectFlags.ColorBit, layout));
            if ((aspects & ImageAspectFlags.DepthBit) != 0)
                preparedFrame.AddDescriptorImageRequirement(new VulkanPreparedDescriptorImageRequirement(imageHandle, resourceGeneration, mipLevel, arrayLayer, ImageAspectFlags.DepthBit, layout));
            if ((aspects & ImageAspectFlags.StencilBit) != 0)
                preparedFrame.AddDescriptorImageRequirement(new VulkanPreparedDescriptorImageRequirement(imageHandle, resourceGeneration, mipLevel, arrayLayer, ImageAspectFlags.StencilBit, layout));
        }

        /// <summary>
        /// Freezes the descriptor payload and sampled-image entry contract used
        /// by a recorded secondary command buffer. The primary recorder uses the
        /// contract to establish exact image layouts before executing the
        /// secondary, while the payload generation prevents reuse after an
        /// update-after-bind descriptor mutation.
        /// </summary>
        private bool CaptureSecondaryDescriptorSetImageRequirements(
            CommandBuffer secondary,
            VulkanPreparedFrameRecording preparedFrame,
            in VulkanPreparedStreamRange payloadRange,
            in VulkanPreparedStreamRange requirementRange,
            out string failureReason)
        {
            failureReason = string.Empty;
            ulong secondaryHandle = unchecked((ulong)secondary.Handle);
            if (secondaryHandle == 0)
            {
                failureReason = "the command buffer handle is null";
                return false;
            }

            if (!CommandBuffers.TrackingBatches.TryGetValue(secondaryHandle, out VulkanCommandBufferTrackingBatch? trackingBatch))
            {
                failureReason = "the secondary has no recording tracking batch";
                return false;
            }
            ulong trackingGeneration;
            lock (trackingBatch)
                trackingGeneration = trackingBatch.RecordingGeneration;

            ReadOnlySpan<VulkanPreparedDescriptorImagePayload> payloads = preparedFrame.GetDescriptorImagePayloads(payloadRange);
            ReadOnlySpan<VulkanPreparedDescriptorImageRequirement> requirements = preparedFrame.GetDescriptorImageRequirements(requirementRange);
            lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            {
                VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
                if (!tracker.CommandBufferLifetimes.TryGetValue(secondaryHandle, out VulkanCommandBufferLifetimeRecord? lifetime) ||
                    lifetime.Level != CommandBufferLevel.Secondary)
                {
                    failureReason = "the secondary has no secondary-level lifetime record";
                    return false;
                }

                for (int index = 0; index < payloads.Length; index++)
                {
                    VulkanPreparedDescriptorImagePayload payload = payloads[index];
                    if (!lifetime.Dependencies.ContainsKey(new VulkanResourceLifetimeKey(ObjectType.DescriptorSet, payload.DescriptorSetHandle)) ||
                        !tracker.PublishedDescriptorSets.TryGetValue(payload.DescriptorSetHandle, out VulkanPublishedDescriptorSetSnapshot? snapshot) ||
                        snapshot.ImagePayloadGeneration != payload.ImagePayloadGeneration)
                    {
                        failureReason = $"descriptor set 0x{payload.DescriptorSetHandle:X} publication changed after prepared-frame capture";
                        return false;
                    }
                }

                lock (Synchronization._vulkanImageLayoutLock)
                {
                    if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(secondaryHandle, out VulkanRecordedImageLayoutState? recorded) ||
                        recorded.RecordingGeneration != trackingGeneration)
                    {
                        failureReason = "the secondary image-layout journal is unavailable or stale";
                        return false;
                    }

                    for (int index = 0; index < payloads.Length; index++)
                    {
                        VulkanPreparedDescriptorImagePayload payload = payloads[index];
                        if (recorded.SecondaryDescriptorImagePayloadGenerations.TryGetValue(payload.DescriptorSetHandle, out ulong existingGeneration) &&
                            existingGeneration != payload.ImagePayloadGeneration)
                        {
                            failureReason = $"descriptor set 0x{payload.DescriptorSetHandle:X} has conflicting prepared payload generations";
                            return false;
                        }
                        recorded.SecondaryDescriptorImagePayloadGenerations[payload.DescriptorSetHandle] = payload.ImagePayloadGeneration;
                    }

                    for (int index = 0; index < requirements.Length; index++)
                    {
                        VulkanPreparedDescriptorImageRequirement requirement = requirements[index];
                        if (!CaptureSecondaryDescriptorImageAspectRequirement(
                                recorded,
                                requirement.ImageHandle,
                                requirement.MipLevel,
                                requirement.ArrayLayer,
                                requirement.AspectMask,
                                requirement.AspectMask,
                                requirement.Layout,
                                requirement.ResourceGeneration))
                        {
                            failureReason = $"prepared descriptor image 0x{requirement.ImageHandle:X} has conflicting subresource requirements";
                            return false;
                        }
                    }
                }
            }

            failureReason = "Ready";
            return true;
        }

        private bool CaptureSecondaryDescriptorSetImageRequirements(
            CommandBuffer secondary,
            DescriptorSet descriptorSet,
            XRFrameBuffer? target,
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata,
            out string failureReason)
        {
            failureReason = string.Empty;
            ulong secondaryHandle = unchecked((ulong)secondary.Handle);
            ulong descriptorSetHandle = descriptorSet.Handle;
            if (secondaryHandle == 0 || descriptorSetHandle == 0)
            {
                failureReason = "the command buffer or descriptor set handle is null";
                return false;
            }

            if (!CommandBuffers.TrackingBatches.TryGetValue(
                    secondaryHandle,
                    out VulkanCommandBufferTrackingBatch? trackingBatch))
            {
                failureReason = "the secondary has no recording tracking batch";
                return false;
            }
            ulong trackingGeneration;
            lock (trackingBatch)
                trackingGeneration = trackingBatch.RecordingGeneration;

            lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            {
                VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
                if (!tracker.CommandBufferLifetimes.TryGetValue(
                        secondaryHandle,
                        out VulkanCommandBufferLifetimeRecord? lifetime))
                {
                    failureReason = "the secondary has no published lifetime record";
                    return false;
                }
                if (lifetime.Level != CommandBufferLevel.Secondary)
                {
                    failureReason = $"the command-buffer lifetime level is {lifetime.Level}";
                    return false;
                }
                if (!lifetime.Dependencies.ContainsKey(
                        new VulkanResourceLifetimeKey(
                            ObjectType.DescriptorSet,
                            descriptorSetHandle)))
                {
                    failureReason = "the recorded dependency snapshot does not contain the descriptor set";
                    return false;
                }
                if (!tracker.PublishedDescriptorSets.TryGetValue(
                        descriptorSetHandle,
                        out VulkanPublishedDescriptorSetSnapshot? snapshot))
                {
                    failureReason = "the descriptor set has no published payload snapshot";
                    return false;
                }
                if (snapshot.Generation == 0)
                {
                    failureReason = "the published descriptor payload generation is zero";
                    return false;
                }

                lock (Synchronization._vulkanImageLayoutLock)
                {
                    if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                            secondaryHandle,
                            out VulkanRecordedImageLayoutState? recorded))
                    {
                        failureReason = "the secondary has no image-layout journal";
                        return false;
                    }
                    if (recorded.RecordingGeneration != trackingGeneration)
                    {
                        failureReason =
                            $"the image-layout journal generation {recorded.RecordingGeneration} does not match tracking generation {trackingGeneration}";
                        return false;
                    }

                    if (recorded.SecondaryDescriptorImagePayloadGenerations.TryGetValue(
                            descriptorSetHandle,
                            out ulong capturedGeneration) &&
                        capturedGeneration != snapshot.ImagePayloadGeneration)
                    {
                        failureReason =
                            $"descriptor image-payload generation changed from {capturedGeneration} to {snapshot.ImagePayloadGeneration} during recording";
                        return false;
                    }
                    recorded.SecondaryDescriptorImagePayloadGenerations[descriptorSetHandle] =
                        snapshot.ImagePayloadGeneration;

                    for (int imageIndex = 0;
                         imageIndex < snapshot.ImageReferences.Length;
                         imageIndex++)
                    {
                        VulkanPublishedDescriptorImageReference published =
                            snapshot.ImageReferences[imageIndex];
                        VulkanDescriptorImageReference imageReference = published.Reference;
                        if (snapshot.HasReflection &&
                            Array.IndexOf(
                                snapshot.ReflectedImageBindings,
                                published.Binding) < 0)
                        {
                            continue;
                        }
                        if (imageReference.Type is not (
                            DescriptorType.CombinedImageSampler or
                            DescriptorType.SampledImage or
                            DescriptorType.InputAttachment))
                        {
                            continue;
                        }
                        if (imageReference.Layout == ImageLayout.Undefined ||
                            !TryGetDescriptorHeapImageViewCreateInfo(
                                imageReference.View,
                                out ImageViewCreateInfo viewInfo) ||
                            viewInfo.Image.Handle == 0)
                        {
                            failureReason =
                                $"binding {published.Binding}[{published.Element}] has no complete image-view publication (view=0x{imageReference.View.Handle:X}, layout={imageReference.Layout})";
                            return false;
                        }

                        ImageSubresourceRange range = viewInfo.SubresourceRange;
                        range.AspectMask = VulkanCommandRuntime.NormalizeBarrierAspectMask(
                            viewInfo.Format,
                            range.AspectMask);
                        range.LevelCount = Math.Max(range.LevelCount, 1u);
                        range.LayerCount = Math.Max(range.LayerCount, 1u);
                        if (IsImageRangeAttachedToFrameBuffer(
                                target,
                                viewInfo.Image,
                                range,
                                imageReference.Layout,
                                passIndex,
                                passMetadata))
                        {
                            continue;
                        }

                        ulong resourceGeneration =
                            ResourceRuntime.GetPublishedGeneration(
                                ObjectType.Image,
                                viewInfo.Image.Handle);
                        if (resourceGeneration == 0)
                        {
                            failureReason =
                                $"binding {published.Binding}[{published.Element}] image 0x{viewInfo.Image.Handle:X} has no published lifetime generation";
                            return false;
                        }

                        for (uint mipOffset = 0;
                             mipOffset < range.LevelCount;
                             mipOffset++)
                        for (uint layerOffset = 0;
                             layerOffset < range.LayerCount;
                             layerOffset++)
                        {
                            uint mipLevel = range.BaseMipLevel + mipOffset;
                            uint arrayLayer = range.BaseArrayLayer + layerOffset;
                            if (!CaptureSecondaryDescriptorImageAspectRequirement(
                                    recorded,
                                    viewInfo.Image.Handle,
                                    mipLevel,
                                    arrayLayer,
                                    range.AspectMask,
                                    ImageAspectFlags.ColorBit,
                                    imageReference.Layout,
                                    resourceGeneration) ||
                                !CaptureSecondaryDescriptorImageAspectRequirement(
                                    recorded,
                                    viewInfo.Image.Handle,
                                    mipLevel,
                                    arrayLayer,
                                    range.AspectMask,
                                    ImageAspectFlags.DepthBit,
                                    imageReference.Layout,
                                    resourceGeneration) ||
                                !CaptureSecondaryDescriptorImageAspectRequirement(
                                    recorded,
                                    viewInfo.Image.Handle,
                                    mipLevel,
                                    arrayLayer,
                                    range.AspectMask,
                                    ImageAspectFlags.StencilBit,
                                    imageReference.Layout,
                                    resourceGeneration))
                            {
                                failureReason =
                                    $"binding {published.Binding}[{published.Element}] publishes conflicting requirements for image 0x{viewInfo.Image.Handle:X} mip {mipLevel} layer {arrayLayer}";
                                return false;
                            }
                        }
                    }
                }
            }

            failureReason = "Ready";
            return true;
        }

        /// <remarks>
        /// The caller holds both the lifetime-tracker and image-layout locks.
        /// </remarks>
        private bool CaptureSecondaryDescriptorImageAspectRequirement(
            VulkanRecordedImageLayoutState recorded,
            ulong imageHandle,
            uint mipLevel,
            uint arrayLayer,
            ImageAspectFlags rangeAspect,
            ImageAspectFlags trackedAspect,
            ImageLayout descriptorLayout,
            ulong resourceGeneration)
        {
            if ((rangeAspect & trackedAspect) == 0)
                return true;

            VulkanTrackedImageSubresource key = new(
                imageHandle,
                mipLevel,
                arrayLayer,
                trackedAspect);
            uint queueFamilyIndex = Vk.QueueFamilyIgnored;
            ulong serial = 0;
            EVulkanExternalImageOwnership ownership =
                EVulkanExternalImageOwnership.EngineOwned;
            if (Synchronization._trackedImageSubresourceStates.TryGetValue(
                    key,
                    out VulkanImageSubresourceState? tracked) &&
                (tracked.Submitted.ResourceGeneration == 0 ||
                 tracked.Submitted.ResourceGeneration == resourceGeneration))
            {
                queueFamilyIndex = tracked.Submitted.QueueFamilyIndex;
                serial = tracked.Submitted.Serial;
                ownership = tracked.Submitted.ExternalOwnership;
            }
            else if (Synchronization._externalImageOwnershipByHandle.TryGetValue(
                         imageHandle,
                         out var external) &&
                     (external.ResourceGeneration == 0 ||
                      external.ResourceGeneration == resourceGeneration))
            {
                ownership = external.Ownership;
            }

            VulkanImageAccessState requiredState = ResolveCommandImageAccessState(
                descriptorLayout,
                trackedAspect,
                queueFamilyIndex: queueFamilyIndex,
                generation: resourceGeneration) with
            {
                Serial = serial,
                ExternalOwnership = ownership,
            };
            if (!recorded.SecondaryDescriptorRequirements.TryGetValue(
                    key,
                    out VulkanImageAccessState existing))
            {
                recorded.SecondaryDescriptorRequirements.Add(key, requiredState);
                return true;
            }

            bool queueFamiliesConflict =
                existing.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                requiredState.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                existing.QueueFamilyIndex != requiredState.QueueFamilyIndex;
            bool resourceGenerationsConflict =
                existing.ResourceGeneration != 0 &&
                requiredState.ResourceGeneration != 0 &&
                existing.ResourceGeneration != requiredState.ResourceGeneration;
            if (existing.Layout != requiredState.Layout ||
                queueFamiliesConflict ||
                resourceGenerationsConflict ||
                existing.ExpectedDescriptorLayout !=
                    requiredState.ExpectedDescriptorLayout ||
                existing.ExternalOwnership != requiredState.ExternalOwnership)
            {
                return false;
            }

            recorded.SecondaryDescriptorRequirements[key] = existing with
            {
                StageMask = existing.StageMask | requiredState.StageMask,
                AccessMask = existing.AccessMask | requiredState.AccessMask,
                QueueFamilyIndex = existing.QueueFamilyIndex != Vk.QueueFamilyIgnored
                    ? existing.QueueFamilyIndex
                    : requiredState.QueueFamilyIndex,
                Serial = Math.Max(existing.Serial, requiredState.Serial),
                ResourceGeneration = existing.ResourceGeneration != 0
                    ? existing.ResourceGeneration
                    : requiredState.ResourceGeneration,
            };
            return true;
        }

        private void TransitionDescriptorTextureForSampling(
            CommandBuffer commandBuffer,
            XRTexture texture,
            XRFrameBuffer? target,
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        {
            if (ResourceRuntime.BackendObjects.Get(texture) is not IVkImageDescriptorSource source ||
                source.DescriptorView.Handle == 0 ||
                !TryGetDescriptorHeapImageViewCreateInfo(source.DescriptorView, out ImageViewCreateInfo viewInfo) ||
                viewInfo.Image.Handle == 0)
            {
                return;
            }

            ImageLayout targetLayout = ResourceRuntime.Descriptors.ResolveDescriptorImageLayout(
                source,
                DescriptorType.CombinedImageSampler);
            TransitionDescriptorImageForSampling(
                commandBuffer,
                source.DescriptorView,
                targetLayout,
                target,
                passIndex,
                passMetadata);
        }

        private unsafe void TransitionDescriptorImageForSampling(
            CommandBuffer commandBuffer,
            ImageView imageView,
            ImageLayout targetLayout,
            XRFrameBuffer? target,
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        {
            if (imageView.Handle == 0 ||
                targetLayout == ImageLayout.Undefined ||
                !TryGetDescriptorHeapImageViewCreateInfo(imageView, out ImageViewCreateInfo viewInfo) ||
                viewInfo.Image.Handle == 0)
            {
                return;
            }

            ImageSubresourceRange range = viewInfo.SubresourceRange;
            range.AspectMask = VulkanCommandRuntime.NormalizeBarrierAspectMask(viewInfo.Format, range.AspectMask);
            range.LevelCount = Math.Max(range.LevelCount, 1u);
            range.LayerCount = Math.Max(range.LayerCount, 1u);
            if (IsImageRangeAttachedToFrameBuffer(
                    target,
                    viewInfo.Image,
                    range,
                    targetLayout,
                    passIndex,
                    passMetadata))
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

            if (FrameDataReuseDiagnosticsEnabled && priorState.Layout == ImageLayout.Undefined)
            {
                Debug.VulkanEvery(
                    $"Vulkan.DescriptorFirstUse.{viewInfo.Image.Handle}.{passIndex}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan.DescriptorFirstUse] Transitioning image=0x{0:X} view=0x{1:X} pass={2} target={3} layout={4}->{5} aspect={6}.",
                    viewInfo.Image.Handle,
                    imageView.Handle,
                    passIndex,
                    target?.Name ?? "<swapchain>",
                    priorState.Layout,
                    targetLayout,
                    range.AspectMask);
            }

            VulkanImageAccessState nextState = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(targetLayout, range.AspectMask);
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
            ImageSubresourceRange range,
            ImageLayout descriptorLayout,
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata)
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
                if (!aspectOverlap || !mipOverlap || !layerOverlap)
                    continue;

                ImageLayout attachmentLayout = vkFbo.ResolveAttachmentReferenceLayoutForPass(
                    i,
                    passIndex,
                    passMetadata);
                bool sharedReadOnlyLayout = attachmentLayout == descriptorLayout &&
                    descriptorLayout is (
                        ImageLayout.DepthStencilReadOnlyOptimal or
                        ImageLayout.DepthReadOnlyOptimal or
                        ImageLayout.StencilReadOnlyOptimal or
                        ImageLayout.ReadOnlyOptimal);
                if (FrameDataReuseDiagnosticsEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.DescriptorAttachmentOverlap.{image.Handle}.{passIndex}.{i}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan.DescriptorAttachmentOverlap] image=0x{0:X} pass={1} target={2} attachment={3} attachmentLayout={4} descriptorLayout={5} sharedReadOnly={6}.",
                        image.Handle,
                        passIndex,
                        target.Name ?? "<unnamed>",
                        i,
                        attachmentLayout,
                        descriptorLayout,
                        sharedReadOnlyLayout);
                }
                if (!sharedReadOnlyLayout)
                    return true;
            }

            return false;
        }

        private static bool PrepareQueryFrameOpsForCommandBufferReuse(
            CommandBuffer commandBuffer,
            FrameOperationSequence ops)
        {
            for (int index = 0; index < ops.Length; index++)
            {
                ref readonly FrameOperationHeader header = ref ops.GetHeader(index);
                if (header.OpCode == EVulkanPrimaryPlanNodeKind.Query &&
                    ops.Stream.GetQuery(index).Operation is (
                        ERenderQueryOperation.Reset or
                        ERenderQueryOperation.Begin or
                        ERenderQueryOperation.WriteTimestamp or
                        ERenderQueryOperation.WriteProperties) &&
                    !ops.Stream.GetQuery(index).Query.PrepareForCommandBufferReuse(commandBuffer))
                {
                    return false;
                }
            }

            return true;
        }

        /// Appends dynamic rendering local-read pNext structs when a pass explicitly
        /// opts into framebuffer-local attachment reads.
        private unsafe bool TryAppendDynamicRenderingLocalReadPNext(
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

        /// <summary>
        /// Rehydrates a frozen local-read mapping into caller-owned stack
        /// storage for secondary-command-buffer inheritance.
        /// </summary>
        internal unsafe bool TryAppendDynamicRenderingLocalReadInheritancePNext(
            in DynamicRenderingLocalReadSignature signature,
            uint colorAttachmentCount,
            ref void* pNext,
            RenderingAttachmentLocationInfo* attachmentLocationInfo,
            RenderingInputAttachmentIndexInfo* inputAttachmentIndexInfo,
            uint* colorAttachmentLocations,
            uint* colorInputAttachmentIndices,
            uint* depthInputAttachmentIndex,
            uint* stencilInputAttachmentIndex)
        {
            if (!signature.Enabled)
                return false;

            Span<uint> attachmentLocations =
                signature.ColorAttachmentLocationCount > 0
                    ? new Span<uint>(
                        colorAttachmentLocations,
                        signature.ColorAttachmentLocationCount)
                    : [];
            Span<uint> inputIndices =
                signature.ColorInputAttachmentIndexCount > 0
                    ? new Span<uint>(
                        colorInputAttachmentIndices,
                        signature.ColorInputAttachmentIndexCount)
                    : [];
            signature.CopyColorAttachmentLocations(
                attachmentLocations);
            signature.CopyColorInputAttachmentIndices(
                inputIndices);

            DynamicRenderingLocalReadPlan localRead = new(
                attachmentLocations,
                inputIndices,
                signature.DepthInputAttachmentIndex,
                signature.StencilInputAttachmentIndex);
            return TryAppendDynamicRenderingLocalReadPNext(
                in localRead,
                colorAttachmentCount,
                ref pNext,
                attachmentLocationInfo,
                inputAttachmentIndexInfo,
                colorAttachmentLocations,
                colorInputAttachmentIndices,
                depthInputAttachmentIndex,
                stencilInputAttachmentIndex);
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
