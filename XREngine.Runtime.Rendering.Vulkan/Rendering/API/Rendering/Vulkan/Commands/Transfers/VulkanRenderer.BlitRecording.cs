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
    internal sealed unsafe partial class VulkanCommandRuntime
    {
        private bool RecordBlitOp(CommandBuffer commandBuffer, uint imageIndex, BlitOp op)
        {
            SwapchainRecordingTarget swapchainTarget = default;
            return RecordBlitOp(commandBuffer, imageIndex, op, in swapchainTarget);
        }

        internal bool RecordBlitOp(
            CommandBuffer commandBuffer,
            uint imageIndex,
            BlitOp op,
            in SwapchainRecordingTarget swapchainTarget)
            => RecordBlitOp(
                commandBuffer,
                imageIndex,
                op,
                in swapchainTarget,
                exactColorSource: null);

        /// <summary>
        /// Blits an already-published presentation source without resolving a
        /// managed framebuffer or texture wrapper again. The tuple's captured
        /// native image and extent remain authoritative for the whole path.
        /// </summary>
        private bool RecordPresentationSourceBlit(
            CommandBuffer commandBuffer,
            uint imageIndex,
            in VulkanPresentationSourceTuple source,
            in SwapchainRecordingTarget swapchainTarget,
            int passIndex,
            in FrameOpContext context)
        {
            BlitOp operation = new(
                passIndex,
                null,
                null,
                0,
                0,
                source.Width,
                source.Height,
                0,
                0,
                swapchainTarget.Extent.Width,
                swapchainTarget.Extent.Height,
                EReadBufferMode.ColorAttachment0,
                ColorBit: true,
                DepthBit: false,
                StencilBit: false,
                LinearFilter: true,
                context);
            BlitImageInfo exactSource = new(
                source.Image,
                source.Format,
                source.Aspect,
                baseArrayLayer: 0,
                layerCount: 1,
                mipLevel: 0,
                new Extent2D(source.Width, source.Height),
                source.ExpectedLayout,
                PipelineStageFlags.FragmentShaderBit,
                AccessFlags.ShaderReadBit,
                samples: source.Samples);
            return RecordBlitOp(
                commandBuffer,
                imageIndex,
                operation,
                in swapchainTarget,
                exactSource);
        }

        private bool RecordBlitOp(
            CommandBuffer commandBuffer,
            uint imageIndex,
            BlitOp op,
            in SwapchainRecordingTarget swapchainTarget,
            BlitImageInfo? exactColorSource)
        {
            bool ExecuteSingleBlit(in BlitImageInfo source, in BlitImageInfo destination, Filter filter)
            {
                BlitImageInfo resolvedSource = RequirePreparedBlitImage(source, "source");
                BlitImageInfo resolvedDestination = RequirePreparedBlitImage(destination, "destination");

                uint commonLayerCount = Math.Min(resolvedSource.LayerCount, resolvedDestination.LayerCount);
                if (commonLayerCount == 0)
                    return false;
                resolvedSource = resolvedSource.WithLayerCount(commonLayerCount);
                resolvedDestination = resolvedDestination.WithLayerCount(commonLayerCount);

                // Validate image handles before issuing Vulkan commands.
                // A stale/destroyed handle causes a native access violation (0xC0000005) in the driver.
                if (resolvedSource.Image.Handle == 0 || resolvedDestination.Image.Handle == 0)
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.NullHandle",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: null image handle. Src=0x{0:X} Dst=0x{1:X} SrcFmt={2} DstFmt={3}",
                        resolvedSource.Image.Handle,
                        resolvedDestination.Image.Handle,
                        resolvedSource.Format,
                        resolvedDestination.Format);
                    return false;
                }

                // Validate blit region dimensions â€” zero-sized regions can crash some drivers.
                if (op.InW == 0 || op.InH == 0 || op.OutW == 0 || op.OutH == 0)
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.ZeroRegion",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: zero-sized region. In={0}x{1} Out={2}x{3}",
                        op.InW, op.InH, op.OutW, op.OutH);
                    return false;
                }

                if (!TryBuildPreparedImageBlit(
                    resolvedSource,
                    resolvedDestination,
                    op.InX,
                    op.InY,
                    op.InW,
                    op.InH,
                    op.OutX,
                    op.OutY,
                    op.OutW,
                    op.OutH,
                    out ImageBlit region))
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.EmptyClampedRegion",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: requested region does not intersect live extents. SrcReq={0},{1}+{2}x{3} SrcExtent={4}x{5} DstReq={6},{7}+{8}x{9} DstExtent={10}x{11}",
                        op.InX,
                        op.InY,
                        op.InW,
                        op.InH,
                        resolvedSource.Extent.Width,
                        resolvedSource.Extent.Height,
                        op.OutX,
                        op.OutY,
                        op.OutW,
                        op.OutH,
                        resolvedDestination.Extent.Width,
                        resolvedDestination.Extent.Height);
                    return false;
                }

                // Derive post-blit target layouts.  PreferredLayout may be Undefined
                // for newly-created dedicated images whose tracked layout hasn't been
                // set yet.  In that case, fall back to the attachment-optimal layout
                // based on the image's aspect mask.
                static ImageLayout DerivePostBlitLayout(in BlitImageInfo info, bool isDestination)
                {
                    if (info.DescriptorSource is { } descriptorSource)
                    {
                        ImageUsageFlags usage = descriptorSource.DescriptorUsage;
                        if ((usage & ImageUsageFlags.StorageBit) != 0)
                            return ImageLayout.General;

                        if ((usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0)
                        {
                            return IsDepthOrStencilAspect(info.AspectMask)
                                ? ImageLayout.DepthStencilReadOnlyOptimal
                                : ImageLayout.ShaderReadOnlyOptimal;
                        }
                    }

                    if (info.PreferredLayout != ImageLayout.Undefined)
                        return info.PreferredLayout;
                    return IsDepthOrStencilAspect(info.AspectMask)
                        ? ImageLayout.DepthStencilAttachmentOptimal
                        : ImageLayout.ColorAttachmentOptimal;
                }

                ImageLayout srcPostLayout = DerivePostBlitLayout(resolvedSource, false);
                ImageLayout dstPostLayout = DerivePostBlitLayout(resolvedDestination, true);

                // Pre-blit: transition from ACTUAL current layout (PreferredLayout)
                // to Transfer-optimal.  For newly-created images this is Undefined,
                // which is a valid OldLayout (content is discarded, which is fine for
                // the destination; for the source, reading from Undefined gives
                // undefined content but won't crash or cause validation errors).
                TransitionPreparedImageForBlit(
                    commandBuffer,
                    resolvedSource,
                    resolvedSource.PreferredLayout,
                    ImageLayout.TransferSrcOptimal,
                    resolvedSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    resolvedSource.StageMask,
                    PipelineStageFlags.TransferBit);

                TransitionPreparedImageForBlit(
                    commandBuffer,
                    resolvedDestination,
                    resolvedDestination.PreferredLayout,
                    ImageLayout.TransferDstOptimal,
                    resolvedDestination.AccessMask,
                    AccessFlags.TransferWriteBit,
                    resolvedDestination.StageMask,
                    PipelineStageFlags.TransferBit);

                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                        "Vulkan.Blit.Record",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] CmdBlitImage: src=0x{0:X}({1}) dst=0x{2:X}({3}) region={4},{5}+{6}x{7}â†’{8},{9}+{10}x{11} filter={12}",
                    resolvedSource.Image.Handle, resolvedSource.Format,
                    resolvedDestination.Image.Handle, resolvedDestination.Format,
                    op.InX, op.InY, op.InW, op.InH,
                        op.OutX, op.OutY, op.OutW, op.OutH,
                        filter);
                }

                PrimaryCommandEncoder.BlitImage(
                    commandBuffer,
                    resolvedSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    resolvedDestination.Image,
                    ImageLayout.TransferDstOptimal,
                    ref region,
                    filter);

                // Post-blit: transition back to the attachment-optimal layout.
                TransitionPreparedImageForBlit(
                    commandBuffer,
                    resolvedSource,
                    ImageLayout.TransferSrcOptimal,
                    srcPostLayout,
                    AccessFlags.TransferReadBit,
                    resolvedSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    resolvedSource.StageMask);

                TransitionPreparedImageForBlit(
                    commandBuffer,
                    resolvedDestination,
                    ImageLayout.TransferDstOptimal,
                    dstPostLayout,
                    AccessFlags.TransferWriteBit,
                    resolvedDestination.AccessMask,
                    PipelineStageFlags.TransferBit,
                    resolvedDestination.StageMask);

                return true;
            }

            bool copiedAny = false;

            BlitImageInfo colorSource = exactColorSource ?? default;
            bool colorSourceReady = exactColorSource.HasValue
                ? colorSource.IsValid
                : TryResolvePreparedBlitImage(op.InFbo, op.ReadBufferMode, wantColor: true, wantDepth: false, wantStencil: false, out colorSource, isSource: true, in swapchainTarget);
            if (op.ColorBit &&
                colorSourceReady &&
                TryResolvePreparedBlitImage(op.OutFbo, EReadBufferMode.ColorAttachment0, wantColor: true, wantDepth: false, wantStencil: false, out var colorDestination, isSource: false, in swapchainTarget))
            {
                copiedAny |= ExecuteSingleBlit(colorSource, colorDestination, op.LinearFilter ? Filter.Linear : Filter.Nearest);
            }

            if ((op.DepthBit || op.StencilBit) &&
                TryResolvePreparedBlitImage(op.InFbo, op.ReadBufferMode, wantColor: false, wantDepth: op.DepthBit, wantStencil: op.StencilBit, out var depthSource, isSource: true, in swapchainTarget) &&
                TryResolvePreparedBlitImage(op.OutFbo, EReadBufferMode.None, wantColor: false, wantDepth: op.DepthBit, wantStencil: op.StencilBit, out var depthDestination, isSource: false, in swapchainTarget))
            {
                // Vulkan only supports nearest filtering for depth/stencil blits.
                copiedAny |= ExecuteSingleBlit(depthSource, depthDestination, Filter.Nearest);
            }

            if (!copiedAny)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.Blit.NoAttachment",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Blit skipped: unable to resolve source/destination attachments for requested masks (Color={0}, Depth={1}, Stencil={2}).",
                    op.ColorBit,
                    op.DepthBit,
                    op.StencilBit);
            }

            return copiedAny;
        }


    }
}
