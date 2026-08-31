using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    internal bool TryBlitTextureToOpenXrSwapchainImage(
        XRTexture2D? sourceTexture,
        Image destinationImage,
        Format destinationFormat,
        Extent2D destinationExtent,
        string destinationLabel)
    {
        if (sourceTexture is null || destinationImage.Handle == 0 || destinationExtent.Width == 0 || destinationExtent.Height == 0)
            return false;

        try
        {
            if (GetOrCreateAPIRenderObject(sourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
                return false;

            if (!source.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye source blit ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Blit.SourceNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye blit source '{0}' is not descriptor-ready.",
                    sourceTexture.Name ?? "<unnamed>");
                return false;
            }

            Image sourceImage = source.DescriptorImage;
            if (sourceImage.Handle == 0)
                return false;

            Extent2D sourceExtent = ResolveOpenXrMirrorDestinationExtent(sourceTexture, source);
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
                return false;

            ImageLayout sourceOldLayout = ResolveOpenXrMirrorDestinationLayout(source);
            if (sourceOldLayout == ImageLayout.Undefined)
                sourceOldLayout = ImageLayout.ColorAttachmentOptimal;

            using VulkanCommandRuntime.CommandScope scope = _commandRuntime.NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                sourceOldLayout,
                ImageLayout.TransferSrcOptimal,
                source.DescriptorAspect);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationFormat,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                ImageAspectFlags.ColorBit);

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect),
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.SrcOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };
            blit.DstOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.DstOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };

            _commandRuntime.BlitImageTracked(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref blit,
                Filter.Nearest);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                ImageLayout.TransferSrcOptimal,
                sourceOldLayout,
                source.DescriptorAspect);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ColorAttachmentOptimal,
                ImageAspectFlags.ColorBit);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Blit.CopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye blit to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    internal bool TryBlitTextureArrayLayerToOpenXrSwapchainImage(
        XRTexture2DArray? sourceTexture,
        uint sourceLayer,
        Image destinationImage,
        Format destinationFormat,
        Extent2D destinationExtent,
        string destinationLabel,
        bool flipY = false)
    {
        if (sourceTexture is null || destinationImage.Handle == 0 || destinationExtent.Width == 0 || destinationExtent.Height == 0)
            return false;

        try
        {
            if (GetOrCreateAPIRenderObject(sourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
                return false;

            uint sourceLayerCount = Math.Max(source.DescriptorArrayLayers, sourceTexture.Depth);
            if (sourceLayer >= sourceLayerCount)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.StereoBlit.LayerOutOfRange.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan stereo blit source layer {0} is out of range for '{1}' ({2} layers).",
                    sourceLayer,
                    sourceTexture.Name ?? "<unnamed>",
                    sourceLayerCount);
                return false;
            }

            if (!source.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan stereo array source blit ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.StereoBlit.SourceNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan stereo blit source '{0}' is not descriptor-ready.",
                    sourceTexture.Name ?? "<unnamed>");
                return false;
            }

            Image sourceImage = source.DescriptorImage;
            if (sourceImage.Handle == 0)
                return false;

            Extent2D sourceExtent = ResolveOpenXrMirrorDestinationExtent(sourceTexture, source, sourceLayer);
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
                return false;

            ImageAspectFlags sourceAspect = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect);
            ImageLayout sourceOldLayout = ResolveOpenXrAttachmentLayout(source, sourceLayer);
            if (sourceOldLayout == ImageLayout.Undefined)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.StereoBlit.SourceLayoutUndefined.{GetHashCode()}.{sourceTexture.GetHashCode()}.{sourceLayer}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan stereo blit source layer {0} of '{1}' had undefined tracked layout before publishing to '{2}'; falling back to ShaderReadOnlyOptimal.",
                    sourceLayer,
                    sourceTexture.Name ?? "<unnamed>",
                    destinationLabel);
                sourceOldLayout = ImageLayout.ShaderReadOnlyOptimal;
            }

            if (TraceOpenXrStereoBlits)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.StereoBlit.Source.{GetHashCode()}.{sourceTexture.GetHashCode()}.{sourceLayer}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan stereo blit source='{0}' layer={1}/{2} oldLayout={3} aspect={4} image=0x{5:X} dst='{6}' dstImage=0x{7:X} extent={8}x{9}",
                    sourceTexture.Name ?? "<unnamed>",
                    sourceLayer,
                    sourceLayerCount,
                    sourceOldLayout,
                    sourceAspect,
                    sourceImage.Handle,
                    destinationLabel,
                    destinationImage.Handle,
                    destinationExtent.Width,
                    destinationExtent.Height);
            }

            using VulkanCommandRuntime.CommandScope scope = _commandRuntime.NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                sourceOldLayout,
                ImageLayout.TransferSrcOptimal,
                sourceAspect,
                sourceLayer,
                1u);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationFormat,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                ImageAspectFlags.ColorBit);

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = sourceAspect,
                    MipLevel = 0,
                    BaseArrayLayer = sourceLayer,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.SrcOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };
            int destinationWidth = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue));
            int destinationHeight = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue));
            blit.DstOffsets.Element0 = new Offset3D
            {
                X = 0,
                Y = flipY ? destinationHeight : 0,
                Z = 0
            };
            blit.DstOffsets.Element1 = new Offset3D
            {
                X = destinationWidth,
                Y = flipY ? 0 : destinationHeight,
                Z = 1
            };

            _commandRuntime.BlitImageTracked(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref blit,
                Filter.Nearest);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                ImageLayout.TransferSrcOptimal,
                sourceOldLayout,
                sourceAspect,
                sourceLayer,
                1u);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ColorAttachmentOptimal,
                ImageAspectFlags.ColorBit);

            if (source is IVkFrameBufferAttachmentSource attachmentSource)
                attachmentSource.UpdateAttachmentTrackedLayout(sourceOldLayout, 0, checked((int)sourceLayer));

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.StereoBlit.CopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan stereo layer blit to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    internal bool TryBlitTextureArrayLayersToOpenXrSwapchainImages(
        XRTexture2DArray? sourceTexture,
        Image leftDestinationImage,
        Format leftDestinationFormat,
        Extent2D leftDestinationExtent,
        string leftDestinationLabel,
        Image rightDestinationImage,
        Format rightDestinationFormat,
        Extent2D rightDestinationExtent,
        string rightDestinationLabel,
        bool flipY = false)
    {
        try
        {
            if (!TryPrepareStereoLayerBlit(
                    sourceTexture,
                    default,
                    leftDestinationImage,
                    leftDestinationFormat,
                    leftDestinationExtent,
                    leftDestinationLabel,
                    rightDestinationImage,
                    rightDestinationFormat,
                    rightDestinationExtent,
                    rightDestinationLabel,
                    flipY,
                    out OpenXrStereoLayerBlitPlan plan))
            {
                return false;
            }

            using VulkanCommandRuntime.CommandScope scope = _commandRuntime.NewCommandScope();
            RecordStereoLayerBlits(scope.CommandBuffer, in plan);
            UpdateStereoLayerBlitTrackedLayouts(in plan);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.StereoBlit.BatchedCopyFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan stereo batched layer blit failed: {0}",
                ex.Message);
            return false;
        }
    }

    private bool TryRecordStereoLayerBlitCommandBuffer(
        in OpenXrStereoLayerBlitPlan plan,
        CommandBuffer predecessorCommandBuffer,
        out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _commandRuntime.Pools.PrimaryGraphics,
            CommandBufferCount = 1,
        };

        Result allocateResult = _commandRuntime.AllocateCommandBufferWithLifetime(ref allocateInfo, out commandBuffer, "OpenXR.CommandBuffer");
        if (allocateResult != Result.Success || commandBuffer.Handle == 0)
        {
            Debug.VulkanWarning($"[OpenXR] Failed to allocate stereo layer publish command buffer: {allocateResult}");
            commandBuffer = default;
            return false;
        }

        bool begun = false;
        try
        {
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            _deviceContext.ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.OpenXR.Blit");
            Result beginResult = _commandRuntime.BeginTrackedCommandBuffer(
                commandBuffer,
                ref beginInfo,
                "OpenXR.Blit");
            if (beginResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to begin stereo layer publish command buffer: {beginResult}");
                FreeOpenXrMirrorPublishCommandBuffer(
                    commandBuffer,
                    EVulkanQueueSubmissionDisposition.Completed);
                commandBuffer = default;
                return false;
            }

            begun = true;
            _commandRuntime.SeedRecordedImageLayoutState(
                commandBuffer,
                predecessorCommandBuffer);
            RecordStereoLayerBlits(commandBuffer, in plan);

            Result endResult = _commandRuntime.EndCommandBufferTracked(commandBuffer);
            if (endResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to end stereo layer publish command buffer: {endResult}");
                FreeOpenXrMirrorPublishCommandBuffer(
                    commandBuffer,
                    EVulkanQueueSubmissionDisposition.Completed);
                commandBuffer = default;
                return false;
            }

            return true;
        }
        catch
        {
            if (begun)
                _commandRuntime.RemoveCommandBufferBindState(commandBuffer);
            FreeOpenXrMirrorPublishCommandBuffer(
                commandBuffer,
                EVulkanQueueSubmissionDisposition.Completed);
            commandBuffer = default;
            throw;
        }
    }

    private bool TryPrepareStereoLayerBlit(
        XRTexture2DArray? sourceTexture,
        CommandBuffer recordedSourceCommandBuffer,
        Image leftDestinationImage,
        Format leftDestinationFormat,
        Extent2D leftDestinationExtent,
        string leftDestinationLabel,
        Image rightDestinationImage,
        Format rightDestinationFormat,
        Extent2D rightDestinationExtent,
        string rightDestinationLabel,
        bool flipY,
        out OpenXrStereoLayerBlitPlan plan)
    {
        plan = default;
        if (sourceTexture is null ||
            leftDestinationImage.Handle == 0 ||
            rightDestinationImage.Handle == 0 ||
            leftDestinationExtent.Width == 0 ||
            leftDestinationExtent.Height == 0 ||
            rightDestinationExtent.Width == 0 ||
            rightDestinationExtent.Height == 0)
        {
            return false;
        }

        // Strict SPS publishes with transfer commands and therefore does not create the
        // per-eye image views used by the direct-render path. Register the runtime-owned
        // images explicitly so a VkImage handle recycled from a completed engine resource
        // receives a fresh lifetime generation before command-buffer dependency tracking.
        _resourceRuntime.RegisterResource(
            ObjectType.Image,
            leftDestinationImage.Handle,
            $"OpenXR.SwapchainImage.{leftDestinationLabel}",
            externallyOwned: true);
        _resourceRuntime.RegisterResource(
            ObjectType.Image,
            rightDestinationImage.Handle,
            $"OpenXR.SwapchainImage.{rightDestinationLabel}",
            externallyOwned: true);

        if (GetOrCreateAPIRenderObject(sourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
            return false;

        uint sourceLayerCount = Math.Max(source.DescriptorArrayLayers, sourceTexture.Depth);
        if (sourceLayerCount < 2)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.StereoBlit.LayerCountTooSmall.{GetHashCode()}.{sourceTexture.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan stereo blit source '{0}' has {1} layer(s); expected at least 2.",
                sourceTexture.Name ?? "<unnamed>",
                sourceLayerCount);
            return false;
        }

        if (!source.TryEnsureDescriptorReadyForUse(
                "OpenXR Vulkan stereo array source batched blit",
                AllowSynchronousResourceUploads))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.StereoBlit.SourceNotReady.{GetHashCode()}.{sourceTexture.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan stereo blit source '{0}' is not descriptor-ready.",
                sourceTexture.Name ?? "<unnamed>");
            return false;
        }

        Image sourceImage = source.DescriptorImage;
        if (sourceImage.Handle == 0)
            return false;

        ImageAspectFlags sourceAspect = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect);
        if (!TryResolveStereoBlitLayer(0, leftDestinationLabel, out Extent2D leftSourceExtent, out ImageLayout leftSourceOldLayout) ||
            !TryResolveStereoBlitLayer(1, rightDestinationLabel, out Extent2D rightSourceExtent, out ImageLayout rightSourceOldLayout))
        {
            return false;
        }

        plan = new OpenXrStereoLayerBlitPlan(
            source,
            sourceImage,
            source.DescriptorFormat,
            sourceAspect,
            leftSourceExtent,
            leftSourceOldLayout,
            rightSourceExtent,
            rightSourceOldLayout,
            leftDestinationImage,
            leftDestinationFormat,
            leftDestinationExtent,
            rightDestinationImage,
            rightDestinationFormat,
            rightDestinationExtent,
            flipY);
        return true;

        bool TryResolveStereoBlitLayer(
            uint sourceLayer,
            string destinationLabel,
            out Extent2D sourceExtent,
            out ImageLayout sourceOldLayout)
        {
            sourceExtent = ResolveOpenXrMirrorDestinationExtent(sourceTexture, source, sourceLayer);
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
            {
                sourceOldLayout = ImageLayout.Undefined;
                return false;
            }

            ImageSubresourceRange sourceRange = new()
            {
                AspectMask = sourceAspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = sourceLayer,
                LayerCount = 1,
            };
            sourceOldLayout = recordedSourceCommandBuffer.Handle != 0 &&
                _commandRuntime.TryGetRecordedImageLayout(
                    recordedSourceCommandBuffer,
                    sourceImage,
                    sourceRange,
                    out ImageLayout recordedSourceLayout)
                    ? recordedSourceLayout
                    : ResolveOpenXrAttachmentLayout(source, sourceLayer);
            if (sourceOldLayout == ImageLayout.Undefined)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.StereoBlit.SourceLayoutUndefined.{GetHashCode()}.{sourceTexture.GetHashCode()}.{sourceLayer}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan stereo blit source layer {0} of '{1}' had undefined tracked layout before publishing to '{2}'; falling back to ShaderReadOnlyOptimal.",
                    sourceLayer,
                    sourceTexture.Name ?? "<unnamed>",
                    destinationLabel);
                sourceOldLayout = ImageLayout.ShaderReadOnlyOptimal;
            }

            if (TraceOpenXrStereoBlits)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.StereoBlit.Source.{GetHashCode()}.{sourceTexture.GetHashCode()}.{sourceLayer}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan stereo blit source='{0}' layer={1}/{2} oldLayout={3} aspect={4} image=0x{5:X} dst='{6}'",
                    sourceTexture.Name ?? "<unnamed>",
                    sourceLayer,
                    sourceLayerCount,
                    sourceOldLayout,
                    sourceAspect,
                    sourceImage.Handle,
                    destinationLabel);
            }
            return true;
        }
    }

    private void RecordStereoLayerBlits(
        CommandBuffer commandBuffer,
        in OpenXrStereoLayerBlitPlan plan)
    {
        EmitStereoLayerBlit(
            commandBuffer,
            plan,
            sourceLayer: 0,
            plan.LeftSourceExtent,
            plan.LeftSourceOldLayout,
            plan.LeftDestinationImage,
            plan.LeftDestinationFormat,
            plan.LeftDestinationExtent);
        EmitStereoLayerBlit(
            commandBuffer,
            plan,
            sourceLayer: 1,
            plan.RightSourceExtent,
            plan.RightSourceOldLayout,
            plan.RightDestinationImage,
            plan.RightDestinationFormat,
            plan.RightDestinationExtent);
    }

    private void EmitStereoLayerBlit(
        CommandBuffer commandBuffer,
        in OpenXrStereoLayerBlitPlan plan,
        uint sourceLayer,
        Extent2D sourceExtent,
        ImageLayout sourceOldLayout,
        Image destinationImage,
        Format destinationFormat,
        Extent2D destinationExtent)
    {
        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            sourceOldLayout,
            ImageLayout.TransferSrcOptimal,
            plan.SourceAspect,
            sourceLayer,
            1u);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            destinationImage,
            destinationFormat,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal,
            ImageAspectFlags.ColorBit);

        ImageBlit blit = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = plan.SourceAspect,
                MipLevel = 0,
                BaseArrayLayer = sourceLayer,
                LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
        blit.SrcOffsets.Element1 = new Offset3D
        {
            X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
            Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
            Z = 1
        };
        int destinationWidth = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue));
        int destinationHeight = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue));
        blit.DstOffsets.Element0 = new Offset3D
        {
            X = 0,
            Y = plan.FlipY ? destinationHeight : 0,
            Z = 0
        };
        blit.DstOffsets.Element1 = new Offset3D
        {
            X = destinationWidth,
            Y = plan.FlipY ? 0 : destinationHeight,
            Z = 1
        };

        _commandRuntime.BlitImageTracked(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            destinationImage,
            ImageLayout.TransferDstOptimal,
            1,
            ref blit,
            Filter.Nearest);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            ImageLayout.TransferSrcOptimal,
            sourceOldLayout,
            plan.SourceAspect,
            sourceLayer,
            1u);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            destinationImage,
            destinationFormat,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageAspectFlags.ColorBit);
    }

    private static void UpdateStereoLayerBlitTrackedLayouts(in OpenXrStereoLayerBlitPlan plan)
    {
        if (plan.Source is not IVkFrameBufferAttachmentSource attachmentSource)
            return;

        attachmentSource.UpdateAttachmentTrackedLayout(plan.LeftSourceOldLayout, 0, 0);
        attachmentSource.UpdateAttachmentTrackedLayout(plan.RightSourceOldLayout, 0, 1);
    }

}
