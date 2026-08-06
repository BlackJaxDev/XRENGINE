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

public unsafe partial class VulkanRenderer
{
    internal bool TryCopyOpenXrEyeSwapchainImageToTexture(
        Image sourceImage,
        Format sourceFormat,
        Extent2D sourceExtent,
        XRTexture2D? destinationTexture,
        string destinationLabel,
        bool flipY = false)
    {
        try
        {
            var request = new OpenXrEyePreviewCopyRequest(
                sourceImage,
                sourceFormat,
                sourceExtent,
                destinationTexture,
                destinationLabel,
                flipY);

            bool prepared = false;
            OpenXrEyePreviewCopyPlan plan = default;
            if (ShouldDeferOpenXrEyePreviewCopyWork(out string resourceWorkReason) &&
                !(prepared = TryPrepareOpenXrEyeSwapchainPreviewCopy(in request, allowDestinationGeneration: false, out plan)))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.DeferCopy.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Deferring Vulkan eye mirror copy to '{0}': {1}",
                    destinationLabel,
                    resourceWorkReason);
                return false;
            }

            if (!prepared &&
                !TryPrepareOpenXrEyeSwapchainPreviewCopy(in request, allowDestinationGeneration: true, out plan))
            {
                return false;
            }

            using CommandScope scope = NewCommandScope();
            RecordOpenXrEyeSwapchainPreviewCopy(scope.CommandBuffer, in plan);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.CopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror copy to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    private bool TryPrepareOpenXrEyeSwapchainPreviewCopy(
        in OpenXrEyePreviewCopyRequest request,
        bool allowDestinationGeneration,
        out OpenXrEyePreviewCopyPlan plan)
    {
        plan = default;
        if (request.SourceImage.Handle == 0 ||
            request.SourceExtent.Width == 0 ||
            request.SourceExtent.Height == 0 ||
            request.DestinationTexture is null)
        {
            return false;
        }

        XRTexture2D destinationTexture = request.DestinationTexture;
        AbstractRenderAPIObject? destinationObject;
        if (allowDestinationGeneration)
        {
            destinationObject = GetOrCreateAPIRenderObject(destinationTexture, generateNow: true);
        }
        else if (!TryGetAPIRenderObject(destinationTexture, out destinationObject) ||
                 destinationObject is null ||
                 !destinationObject.IsGenerated)
        {
            return false;
        }

        if (destinationObject is not IVkImageDescriptorSource destinationSource)
            return false;

        if (!destinationSource.TryEnsureDescriptorReadyForUse(
                $"OpenXR Vulkan eye mirror copy ({request.DestinationLabel})",
                AllowSynchronousResourceUploads))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.DestinationNotReady.{GetHashCode()}.{request.DestinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror target '{0}' is not descriptor-ready.",
                request.DestinationLabel);
            return false;
        }

        Image destinationImage = destinationSource.DescriptorImage;
        if (destinationImage.Handle == 0)
            return false;

        Extent2D destinationExtent = ResolveOpenXrMirrorDestinationExtent(request.DestinationTexture, destinationSource);
        if (destinationExtent.Width == 0 || destinationExtent.Height == 0)
            return false;

        plan = new OpenXrEyePreviewCopyPlan(
            request.SourceImage,
            request.SourceFormat,
            request.SourceExtent,
            ResolveOpenXrSwapchainImageTrackedLayout(request.SourceImage),
            destinationSource,
            destinationImage,
            destinationExtent,
            ResolveOpenXrMirrorDestinationLayout(destinationSource),
            NormalizeOpenXrMirrorAspect(destinationSource.DescriptorFormat, destinationSource.DescriptorAspect),
            request.DestinationLabel,
            request.FlipY);
        return true;
    }

    private void RecordOpenXrEyeSwapchainPreviewCopy(
        CommandBuffer commandBuffer,
        in OpenXrEyePreviewCopyPlan plan)
    {
        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            plan.SourceOldLayout,
            ImageLayout.TransferSrcOptimal,
            ImageAspectFlags.ColorBit);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.DestinationImage,
            plan.DestinationSource.DescriptorFormat,
            plan.DestinationOldLayout,
            ImageLayout.TransferDstOptimal,
            plan.DestinationAspect);

        ImageBlit blit = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = plan.DestinationAspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
        blit.SrcOffsets.Element1 = new Offset3D
        {
            X = checked((int)Math.Min(plan.SourceExtent.Width, (uint)int.MaxValue)),
            Y = checked((int)Math.Min(plan.SourceExtent.Height, (uint)int.MaxValue)),
            Z = 1
        };

        int destinationWidth = checked((int)Math.Min(plan.DestinationExtent.Width, (uint)int.MaxValue));
        int destinationHeight = checked((int)Math.Min(plan.DestinationExtent.Height, (uint)int.MaxValue));
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

        CmdBlitImageTracked(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            plan.DestinationImage,
            ImageLayout.TransferDstOptimal,
            1,
            ref blit,
            Filter.Nearest);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageAspectFlags.ColorBit);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.DestinationImage,
            plan.DestinationSource.DescriptorFormat,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            plan.DestinationAspect);

        if (plan.DestinationSource is IVkFrameBufferAttachmentSource attachmentSource)
            attachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, 0, 0);
    }

    internal bool TryPublishOpenXrEyeMirrorTextures(
        in OpenXrEyeMirrorPublishRequest firstEye,
        in OpenXrEyeMirrorPublishRequest secondEye,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        firstPreviewCopied = false;
        secondPreviewCopied = false;

        try
        {
            if (!TryPrepareOpenXrEyeMirrorPublish(firstEye, out OpenXrEyeMirrorPublishPlan firstPlan) ||
                !TryPrepareOpenXrEyeMirrorPublish(secondEye, out OpenXrEyeMirrorPublishPlan secondPlan))
            {
                return false;
            }

            using CommandScope scope = NewCommandScope();
            RecordOpenXrEyeMirrorPublish(scope.CommandBuffer, in firstPlan, out firstPreviewCopied);
            RecordOpenXrEyeMirrorPublish(scope.CommandBuffer, in secondPlan, out secondPreviewCopied);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.BatchPublishFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror batch publish failed: {0}",
                ex.Message);
            return false;
        }
    }

    private bool TryRecordOpenXrEyeMirrorPublishCommandBuffer(
        in OpenXrEyeMirrorPublishPlan firstPlan,
        in OpenXrEyeMirrorPublishPlan secondPlan,
        out CommandBuffer commandBuffer,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        commandBuffer = default;
        firstPreviewCopied = false;
        secondPreviewCopied = false;

        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };

        Result allocateResult = AllocateVulkanCommandBuffersTracked(ref allocateInfo, out commandBuffer, "OpenXR.CommandBuffer");
        if (allocateResult != Result.Success || commandBuffer.Handle == 0)
        {
            Debug.VulkanWarning($"[OpenXR] Failed to allocate eye mirror publish command buffer: {allocateResult}");
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

            ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.OpenXR.PreviewPublish");
            Result beginResult = Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);
            if (beginResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to begin eye mirror publish command buffer: {beginResult}");
                FreeOpenXrMirrorPublishCommandBuffer(
                    commandBuffer,
                    EVulkanQueueSubmissionDisposition.Completed);
                commandBuffer = default;
                return false;
            }

            begun = true;
            ResetCommandBufferBindState(commandBuffer);
            RecordOpenXrEyeMirrorPublish(commandBuffer, in firstPlan, out firstPreviewCopied);
            RecordOpenXrEyeMirrorPublish(commandBuffer, in secondPlan, out secondPreviewCopied);

            Result endResult = EndCommandBufferTracked(commandBuffer);
            if (endResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to end eye mirror publish command buffer: {endResult}");
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
                RemoveCommandBufferBindState(commandBuffer);
            FreeOpenXrMirrorPublishCommandBuffer(
                commandBuffer,
                EVulkanQueueSubmissionDisposition.Completed);
            commandBuffer = default;

            throw;
        }
    }

    private void FreeOpenXrMirrorPublishCommandBuffer(
        CommandBuffer commandBuffer,
        EVulkanQueueSubmissionDisposition submissionDisposition)
    {
        if (commandBuffer.Handle == 0)
            return;

        if (!ShouldFreeTemporaryOpenXrCommandBuffer(submissionDisposition))
        {
            RemoveCommandBufferBindState(commandBuffer);
            return;
        }

        FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "OpenXR.Temporary");
        RemoveCommandBufferBindState(commandBuffer);
    }

    private bool TryPrepareOpenXrEyeMirrorPublish(
        in OpenXrEyeMirrorPublishRequest request,
        out OpenXrEyeMirrorPublishPlan plan)
    {
        plan = default;
        if (request.SourceTexture is null ||
            request.SwapchainImage.Handle == 0 ||
            request.Extent.Width == 0 ||
            request.Extent.Height == 0)
        {
            return false;
        }

        if (GetOrCreateAPIRenderObject(request.SourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
            return false;

        if (!source.TryEnsureDescriptorReadyForUse(
                $"OpenXR Vulkan eye mirror publish source ({request.DestinationLabel})",
                AllowSynchronousResourceUploads))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.PublishSourceNotReady.{GetHashCode()}.{request.DestinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror publish source '{0}' is not descriptor-ready.",
                request.SourceTexture.Name ?? "<unnamed>");
            return false;
        }

        Image sourceImage = source.DescriptorImage;
        if (sourceImage.Handle == 0)
            return false;

        Extent2D sourceExtent = ResolveOpenXrMirrorDestinationExtent(request.SourceTexture, source);
        if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
            return false;

        ImageLayout sourceOldLayout = ResolveOpenXrMirrorDestinationLayout(source);
        if (sourceOldLayout == ImageLayout.Undefined)
            sourceOldLayout = ImageLayout.ShaderReadOnlyOptimal;

        ImageAspectFlags sourceAspect = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect);

        IVkImageDescriptorSource? previewSource = null;
        Image previewImage = default;
        Extent2D previewExtent = default;
        ImageLayout previewOldLayout = ImageLayout.Undefined;
        ImageAspectFlags previewAspect = ImageAspectFlags.ColorBit;

        if (request.PreviewTexture is not null)
        {
            if (GetOrCreateAPIRenderObject(request.PreviewTexture, generateNow: true) is IVkImageDescriptorSource destination &&
                destination.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye mirror publish preview ({request.DestinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                previewImage = destination.DescriptorImage;
                previewExtent = ResolveOpenXrMirrorDestinationExtent(request.PreviewTexture, destination);
                if (previewImage.Handle != 0 && previewExtent.Width > 0 && previewExtent.Height > 0)
                {
                    previewSource = destination;
                    previewOldLayout = ResolveOpenXrMirrorDestinationLayout(destination);
                    previewAspect = NormalizeOpenXrMirrorAspect(destination.DescriptorFormat, destination.DescriptorAspect);
                }
            }

            if (previewSource is null)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.PublishPreviewNotReady.{GetHashCode()}.{request.DestinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye mirror preview target '{0}' is not descriptor-ready.",
                    request.PreviewTexture.Name ?? "<unnamed>");
            }
        }

        plan = new OpenXrEyeMirrorPublishPlan(
            source,
            sourceImage,
            source.DescriptorFormat,
            sourceExtent,
            sourceOldLayout,
            sourceAspect,
            request.SwapchainImage,
            request.SwapchainFormat,
            request.Extent,
            previewSource,
            previewImage,
            previewExtent,
            previewOldLayout,
            previewAspect,
            request.DestinationLabel,
            request.FlipPreviewY);
        return true;
    }

    private void RecordOpenXrEyeMirrorPublish(
        CommandBuffer commandBuffer,
        in OpenXrEyeMirrorPublishPlan plan,
        out bool previewCopied)
    {
        previewCopied = false;

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            plan.SourceOldLayout,
            ImageLayout.TransferSrcOptimal,
            plan.SourceAspect);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SwapchainImage,
            plan.SwapchainFormat,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal,
            ImageAspectFlags.ColorBit);

        ImageBlit swapchainBlit = CreateOpenXrMirrorBlit(
            plan.SourceAspect,
            ImageAspectFlags.ColorBit,
            plan.SourceExtent,
            plan.SwapchainExtent,
            flipDestinationY: false);

        CmdBlitImageTracked(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            plan.SwapchainImage,
            ImageLayout.TransferDstOptimal,
            1,
            ref swapchainBlit,
            Filter.Nearest);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SwapchainImage,
            plan.SwapchainFormat,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageAspectFlags.ColorBit);

        if (plan.PreviewSource is not null && plan.PreviewImage.Handle != 0)
        {
            TransitionOpenXrMirrorImage(
                commandBuffer,
                plan.PreviewImage,
                plan.PreviewSource.DescriptorFormat,
                plan.PreviewOldLayout,
                ImageLayout.TransferDstOptimal,
                plan.PreviewAspect);

            ImageBlit previewBlit = CreateOpenXrMirrorBlit(
                plan.SourceAspect,
                plan.PreviewAspect,
                plan.SourceExtent,
                plan.PreviewExtent,
                plan.FlipPreviewY);

            CmdBlitImageTracked(
                commandBuffer,
                plan.SourceImage,
                ImageLayout.TransferSrcOptimal,
                plan.PreviewImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref previewBlit,
                Filter.Nearest);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                plan.PreviewImage,
                plan.PreviewSource.DescriptorFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                plan.PreviewAspect);

            if (plan.PreviewSource is IVkFrameBufferAttachmentSource previewAttachmentSource)
                previewAttachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, 0, 0);

            previewCopied = true;
        }

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            ImageLayout.TransferSrcOptimal,
            plan.SourceOldLayout,
            plan.SourceAspect);

        if (plan.Source is IVkFrameBufferAttachmentSource sourceAttachmentSource)
            sourceAttachmentSource.UpdateAttachmentTrackedLayout(plan.SourceOldLayout, 0, 0);
    }

    private static ImageBlit CreateOpenXrMirrorBlit(
        ImageAspectFlags sourceAspect,
        ImageAspectFlags destinationAspect,
        Extent2D sourceExtent,
        Extent2D destinationExtent,
        bool flipDestinationY)
    {
        ImageBlit blit = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = sourceAspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = destinationAspect,
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
            Y = flipDestinationY ? destinationHeight : 0,
            Z = 0
        };
        blit.DstOffsets.Element1 = new Offset3D
        {
            X = destinationWidth,
            Y = flipDestinationY ? 0 : destinationHeight,
            Z = 1
        };

        return blit;
    }

    internal bool TryCopyOpenXrEyeMirrorTexture(
        XRTexture? sourceTexture,
        XRTexture2D? destinationTexture,
        string destinationLabel,
        bool flipY = false)
    {
        if (IsDeviceLost || sourceTexture is null || destinationTexture is null)
            return false;

        try
        {
            if (GetOrCreateAPIRenderObject(sourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
                return false;

            if (!source.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye mirror source copy ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.SourceNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye mirror source '{0}' is not descriptor-ready.",
                    sourceTexture.Name ?? "<unnamed>");
                return false;
            }

            if (GetOrCreateAPIRenderObject(destinationTexture, generateNow: true) is not IVkImageDescriptorSource destination)
                return false;

            if (!destination.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye mirror destination copy ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.DestinationNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye mirror destination '{0}' is not descriptor-ready.",
                    destinationTexture.Name ?? "<unnamed>");
                return false;
            }

            Image sourceImage = source.DescriptorImage;
            Image destinationImage = destination.DescriptorImage;
            if (sourceImage.Handle == 0 || destinationImage.Handle == 0)
                return false;

            Extent2D sourceExtent = ResolveOpenXrMirrorSourceExtent(sourceTexture, source);
            Extent2D destinationExtent = ResolveOpenXrMirrorDestinationExtent(destinationTexture, destination);
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0 ||
                destinationExtent.Width == 0 || destinationExtent.Height == 0)
                return false;

            ImageLayout sourceOldLayout = ResolveOpenXrMirrorDestinationLayout(source);
            if (sourceOldLayout == ImageLayout.Undefined)
                sourceOldLayout = ImageLayout.ColorAttachmentOptimal;

            ImageLayout destinationOldLayout = ResolveOpenXrMirrorDestinationLayout(destination);

            ImageAspectFlags sourceAspect = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect);
            ImageAspectFlags destinationAspect = NormalizeOpenXrMirrorAspect(destination.DescriptorFormat, destination.DescriptorAspect);
            uint sourceBaseArrayLayer = ResolveOpenXrMirrorBaseArrayLayer(sourceTexture);

            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                sourceOldLayout,
                ImageLayout.TransferSrcOptimal,
                sourceAspect,
                sourceBaseArrayLayer,
                1u);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destination.DescriptorFormat,
                destinationOldLayout,
                ImageLayout.TransferDstOptimal,
                destinationAspect);

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = sourceAspect,
                    MipLevel = 0,
                    BaseArrayLayer = sourceBaseArrayLayer,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = destinationAspect,
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

            CmdBlitImageTracked(
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
                sourceBaseArrayLayer,
                1u);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destination.DescriptorFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                destinationAspect);

            if (destination is IVkFrameBufferAttachmentSource destinationAttachmentSource)
                destinationAttachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, 0, 0);
            if (source is IVkFrameBufferAttachmentSource sourceAttachmentSource)
                sourceAttachmentSource.UpdateAttachmentTrackedLayout(sourceOldLayout, 0, 0);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.TextureCopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror texture copy to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    private static uint ResolveOpenXrMirrorBaseArrayLayer(XRTexture texture)
        => texture is XRTextureViewBase view ? view.MinLayer : 0u;

}
