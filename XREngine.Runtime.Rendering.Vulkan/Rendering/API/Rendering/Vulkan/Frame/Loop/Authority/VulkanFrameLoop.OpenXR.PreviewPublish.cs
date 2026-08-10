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

internal sealed unsafe partial class VulkanFrameLoop
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

            return _commandRuntime.ExecuteOpenXrPreviewCopy(in plan);
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

            return _commandRuntime.ExecuteOpenXrMirrorPublish(
                in firstPlan,
                in secondPlan,
                out firstPreviewCopied,
                out secondPreviewCopied);
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
        => _commandRuntime.TryRecordOpenXrEyeMirrorPublishCommandBuffer(
            in firstPlan,
            in secondPlan,
            out commandBuffer,
            out firstPreviewCopied,
            out secondPreviewCopied);

    private void FreeOpenXrMirrorPublishCommandBuffer(
        CommandBuffer commandBuffer,
        EVulkanQueueSubmissionDisposition submissionDisposition)
        => _commandRuntime.ReleaseOpenXrTemporaryCommandBuffer(
            commandBuffer,
            submissionDisposition);

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

            return _commandRuntime.ExecuteOpenXrMirrorTextureCopy(
                source,
                sourceImage,
                source.DescriptorFormat,
                sourceExtent,
                sourceOldLayout,
                sourceAspect,
                sourceBaseArrayLayer,
                destination,
                destinationImage,
                destinationExtent,
                destinationOldLayout,
                destinationAspect,
                flipY);
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
