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
    internal bool TryClearOpenXrSwapchainImage(Image image, Extent2D extent, ColorF4 color)
    {
        if (image.Handle == 0 || extent.Width == 0 || extent.Height == 0)
            return false;

        try
        {
            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            ImageSubresourceRange range = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageMemoryBarrier toTransfer = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            ClearColorValue clearColor = new(color.R, color.G, color.B, color.A);
            CmdClearColorImageTracked(commandBuffer, image, ImageLayout.TransferDstOptimal, ref clearColor, 1, ref range);

            ImageMemoryBarrier toColorAttachment = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ColorAttachmentReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toColorAttachment);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.ClearFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan swapchain diagnostic clear failed: {0}",
                ex.Message);
            return false;
        }
    }

    private static Extent2D ResolveOpenXrMirrorDestinationExtent(
        XRTexture2D destinationTexture,
        IVkImageDescriptorSource destinationSource)
    {
        return destinationSource is IVkFrameBufferAttachmentSource attachmentSource &&
            attachmentSource.TryGetAttachmentExtent(0, 0, out Extent2D attachmentExtent) &&
            attachmentExtent.Width > 0 &&
            attachmentExtent.Height > 0
                ? attachmentExtent
                : new Extent2D(
                Math.Max(destinationTexture.Width, 1u),
                Math.Max(destinationTexture.Height, 1u));
    }

    private static Extent2D ResolveOpenXrMirrorSourceExtent(
        XRTexture sourceTexture,
        IVkImageDescriptorSource source)
    {
        if (source is IVkFrameBufferAttachmentSource attachmentSource &&
            attachmentSource.TryGetAttachmentExtent(0, 0, out Extent2D attachmentExtent) &&
            attachmentExtent.Width > 0 &&
            attachmentExtent.Height > 0)
        {
            return attachmentExtent;
        }

        return sourceTexture switch
        {
            XRTexture2D texture2D => new Extent2D(
                Math.Max(texture2D.Width, 1u),
                Math.Max(texture2D.Height, 1u)),
            XRTexture2DArray textureArray => new Extent2D(
                Math.Max(textureArray.Width, 1u),
                Math.Max(textureArray.Height, 1u)),
            XRTexture2DArrayView textureArrayView => new Extent2D(
                Math.Max(textureArrayView.Width, 1u),
                Math.Max(textureArrayView.Height, 1u)),
            _ => new Extent2D(1u, 1u)
        };
    }

    private static Extent2D ResolveOpenXrMirrorDestinationExtent(
        XRTexture2DArray destinationTexture,
        IVkImageDescriptorSource destinationSource,
        uint layer)
    {
        return destinationSource is IVkFrameBufferAttachmentSource attachmentSource &&
            attachmentSource.TryGetAttachmentExtent(0, checked((int)layer), out Extent2D attachmentExtent) &&
            attachmentExtent.Width > 0 &&
            attachmentExtent.Height > 0
                ? attachmentExtent
                : new Extent2D(
                    Math.Max(destinationTexture.Width, 1u),
                    Math.Max(destinationTexture.Height, 1u));
    }

    private ImageLayout ResolveOpenXrAttachmentLayout(
        IVkImageDescriptorSource source,
        uint layer)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect),
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = layer,
            LayerCount = 1,
        };
        if (source.DescriptorImage.Handle != 0 &&
            TryGetTrackedImageLayout(source.DescriptorImage, range, out ImageLayout liveLayout) &&
            liveLayout != ImageLayout.Undefined)
        {
            return liveLayout;
        }

        ImageLayout layout = ImageLayout.Undefined;
        if (source is IVkFrameBufferAttachmentSource attachmentSource)
            layout = attachmentSource.GetAttachmentTrackedLayout(0, checked((int)layer));

        if (layout == ImageLayout.Undefined)
            layout = source.TrackedImageLayout;

        return layout;
    }

    private static ImageLayout ResolveOpenXrMirrorDestinationLayout(IVkImageDescriptorSource destinationSource)
    {
        ImageLayout layout = ImageLayout.Undefined;
        if (destinationSource is IVkFrameBufferAttachmentSource attachmentSource)
            layout = attachmentSource.GetAttachmentTrackedLayout(0, 0);

        if (layout == ImageLayout.Undefined)
            layout = destinationSource.TrackedImageLayout;

        return layout;
    }

    private ImageLayout ResolveOpenXrSwapchainImageTrackedLayout(Image image)
    {
        if (image.Handle == 0)
            return ImageLayout.ColorAttachmentOptimal;

        ImageSubresourceRange colorRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        return TryGetTrackedImageLayout(image, colorRange, out ImageLayout trackedLayout) &&
            trackedLayout != ImageLayout.Undefined
                ? trackedLayout
                : ImageLayout.ColorAttachmentOptimal;
    }

    private static ImageAspectFlags NormalizeOpenXrMirrorAspect(Format format, ImageAspectFlags aspect)
    {
        if (!IsDepthStencilFormat(format))
            return ImageAspectFlags.ColorBit;

        ImageAspectFlags normalized = aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit);
        return normalized == ImageAspectFlags.None ? ImageAspectFlags.DepthBit : normalized;
    }

    private void TransitionOpenXrMirrorImage(
        CommandBuffer commandBuffer,
        Image image,
        Format format,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageAspectFlags aspectMask)
        => TransitionOpenXrMirrorImage(
            commandBuffer,
            image,
            format,
            oldLayout,
            newLayout,
            aspectMask,
            baseArrayLayer: 0u,
            layerCount: 1u);

    private void TransitionOpenXrMirrorImage(
        CommandBuffer commandBuffer,
        Image image,
        Format format,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageAspectFlags aspectMask,
        uint baseArrayLayer,
        uint layerCount)
    {
        if (oldLayout == newLayout)
            return;

        OpenXrMirrorBarrierAccess(oldLayout, out AccessFlags srcAccess, out PipelineStageFlags srcStage);
        OpenXrMirrorBarrierAccess(newLayout, out AccessFlags dstAccess, out PipelineStageFlags dstStage);

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = NormalizeOpenXrMirrorAspect(format, aspectMask),
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = Math.Max(layerCount, 1u)
            }
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }

    private static void OpenXrMirrorBarrierAccess(
        ImageLayout layout,
        out AccessFlags access,
        out PipelineStageFlags stage)
    {
        switch (layout)
        {
            case ImageLayout.Undefined:
                access = 0;
                stage = PipelineStageFlags.TopOfPipeBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                access = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
                stage = PipelineStageFlags.ColorAttachmentOutputBit;
                break;
            case ImageLayout.TransferSrcOptimal:
                access = AccessFlags.TransferReadBit;
                stage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.TransferDstOptimal:
                access = AccessFlags.TransferWriteBit;
                stage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.ShaderReadOnlyOptimal:
                access = AccessFlags.ShaderReadBit;
                stage = PipelineStageFlags.FragmentShaderBit;
                break;
            case ImageLayout.General:
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                stage = PipelineStageFlags.AllCommandsBit;
                break;
            default:
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                stage = PipelineStageFlags.AllCommandsBit;
                break;
        }
    }

    private void DrainRetiredResourcesFromCompletedSubmittedFrameSlots()
    {
        using DesktopFrameRetirementScope retirement =
            EnterDesktopFrameRetirementScope();
        ReadOnlySpan<ulong> timelineValues = retirement.TimelineValues;
        if (timelineValues.IsEmpty)
        {
            DrainCompletedRecordedTextureUploadPublications();
            return;
        }

        int frameSlotCount = Math.Min(
            timelineValues.Length,
            MAX_FRAMES_IN_FLIGHT);
        DesktopFrameActivitySnapshot desktopActivity =
            CaptureDesktopFrameActivity();
        Span<bool> drainableSlots =
            stackalloc bool[MAX_FRAMES_IN_FLIGHT];
        for (int i = 0; i < frameSlotCount; i++)
        {
            if (desktopActivity.IsActive &&
                i == desktopActivity.FrameSlot)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.ActiveDesktopFrameSlotDrainSkipped.{GetHashCode()}.{i}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan skipped retired-resource drain for active desktop frame slot {0} while desktop frame {1} is recording.",
                    i,
                    desktopActivity.FrameNumber);
                continue;
            }

            ulong value = timelineValues[i];
            if (value != 0 &&
                !HasTimelineValueCompleted(
                    retirement.TimelineSemaphore,
                    value))
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.PendingFrameSlotDrainSkipped.{GetHashCode()}.{i}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan skipped retired-resource drain before eye rendering because frame slot {0} is still pending at timeline value {1}.",
                    i,
                    value);
                continue;
            }

            drainableSlots[i] = true;
        }

        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredCommandBuffers(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredCommandPools(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredDescriptorSets(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredDescriptorPools(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredPipelines(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredPipelineLayouts(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredDescriptorSetLayouts(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredQueryPools(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredBufferViews(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredFramebuffers(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredBuffers(i, int.MaxValue);
        for (int pass = 0; pass < frameSlotCount; pass++)
            for (int i = 0; i < frameSlotCount; i++)
                if (drainableSlots[i])
                    DrainRetiredImages(i, int.MaxValue);

        DrainCompletedRecordedTextureUploadPublications();
    }
    private void WaitForOpenXrFrameDataSlot(uint frameDataImageIndex, string reason)
    {
        ulong value;
        Silk.NET.Vulkan.Semaphore timelineSemaphore;
        using (DesktopFrameRetirementScope retirement =
               EnterDesktopFrameRetirementScope())
        {
            ReadOnlySpan<ulong> timelineValues = retirement.TimelineValues;
            timelineSemaphore = retirement.TimelineSemaphore;
            if (timelineSemaphore.Handle == 0 ||
                frameDataImageIndex >= timelineValues.Length)
            {
                return;
            }

            value = timelineValues[(int)frameDataImageIndex];
            if (value == 0 || HasTimelineValueCompleted(timelineSemaphore, value))
                return;
        }

        Debug.VulkanEvery(
            $"OpenXR.Vulkan.WaitFrameDataSlot.{GetHashCode()}.{frameDataImageIndex}.{reason}",
            TimeSpan.FromSeconds(1),
            "[OpenXR] Vulkan waiting for frame-data slot {0} before {1}; pending timeline value {2}.",
            frameDataImageIndex,
            reason,
            value);

        WaitForTimelineValue(timelineSemaphore, value);
    }
    private ImageView GetOrCreateOpenXrSwapchainImageView(Image image, Format format)
    {
        ulong key = image.Handle;
        if (_openXrBackend.SwapchainImageViews.TryGetValue(key, out VulkanOpenXrSwapchainImageViewCacheEntry cached))
        {
            if (cached.Format == format && cached.View.Handle != 0)
                return cached.View;

            if (cached.View.Handle != 0 && TryBeginDestroyImageView(cached.View, "OpenXR.SwapchainImageViewFormatChanged"))
                Api!.DestroyImageView(device, cached.View, null);
            _openXrBackend.SwapchainImageViews.Remove(key);
        }

        ImageView imageView = CreateOpenXrSwapchainImageView(image, format);
        _openXrBackend.SwapchainImageViews[key] = new VulkanOpenXrSwapchainImageViewCacheEntry(imageView, format);
        return imageView;
    }

    private ImageView CreateOpenXrSwapchainImageView(Image image, Format format)
    {
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            }
        };

        if (Api!.CreateImageView(device, ref viewInfo, null, out ImageView imageView) != Result.Success)
            throw new InvalidOperationException("Failed to create OpenXR Vulkan swapchain image view.");

        TrackLiveImageView(imageView, in viewInfo, "OpenXR.SwapchainImageView");
        SetDebugObjectName(ObjectType.ImageView, imageView.Handle, $"OpenXR.SwapchainImageView.0x{image.Handle:X}.{format}");
        return imageView;
    }

    private VulkanOpenXrDepthTarget GetOrCreateOpenXrDepthTarget(uint openXrViewIndex, Extent2D extent)
    {
        int targetIndex = ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex);
        ref VulkanOpenXrDepthTarget cachedTarget = ref _openXrBackend.CachedDepthTargets[targetIndex];
        ref Extent2D cachedExtent = ref _openXrBackend.CachedDepthExtents[targetIndex];
        if (cachedTarget.Image.Handle != 0 &&
            cachedExtent.Width == extent.Width &&
            cachedExtent.Height == extent.Height)
            return cachedTarget;

        DestroyOpenXrDepthTarget(cachedTarget);
        cachedTarget = CreateOpenXrDepthTarget(extent);
        cachedExtent = extent;
        return cachedTarget;
    }

    private VulkanOpenXrDepthTarget CreateOpenXrDepthTarget(Extent2D extent)
    {
        Format depthFormat = FindDepthFormat();
        ImageAspectFlags depthAspect = IsDepthStencilFormat(depthFormat)
            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : ImageAspectFlags.DepthBit;

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = depthFormat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        Image depthImage = default;
        ImageView depthView = default;
        VulkanMemoryAllocation allocation = VulkanMemoryAllocation.Null;
        try
        {
            if (CreateVulkanImageTracked(ref imageInfo, out depthImage, "OpenXR.DepthTarget") != Result.Success)
                throw new InvalidOperationException("Failed to create OpenXR Vulkan depth image.");

            ClearTrackedImageLayouts(depthImage);
            allocation = AllocateImageMemoryWithFallback(depthImage, MemoryPropertyFlags.DeviceLocalBit);
            _imageAllocationTracker.Allocations[depthImage.Handle] = allocation;

            if (Api!.BindImageMemory(device, depthImage, allocation.Memory, allocation.Offset) != Result.Success)
                throw new InvalidOperationException("Failed to bind OpenXR Vulkan depth image memory.");

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = depthImage,
                ViewType = ImageViewType.Type2D,
                Format = depthFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = depthAspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (Api!.CreateImageView(device, ref viewInfo, null, out depthView) != Result.Success)
                throw new InvalidOperationException("Failed to create OpenXR Vulkan depth image view.");

            TrackLiveImageView(depthView, in viewInfo, "OpenXR.DepthTarget");
            return new VulkanOpenXrDepthTarget(depthImage, allocation.Memory, depthView, depthFormat, depthAspect);
        }
        catch
        {
            if (depthView.Handle != 0 && TryBeginDestroyImageView(depthView, "CreateOpenXrDepthTargetFailed"))
                Api!.DestroyImageView(device, depthView, null);

            if (depthImage.Handle != 0)
            {
                bool hasTrackedAllocation = _imageAllocationTracker.Allocations.TryRemove(
                    depthImage.Handle,
                    out VulkanMemoryAllocation trackedAllocation);
                DestroyVulkanImageImmediateTracked(depthImage, "CreateOpenXrDepthTargetFailed");
                FreeMemoryAllocation(hasTrackedAllocation ? trackedAllocation : allocation);
            }

            throw;
        }
    }

    private void DestroyOpenXrDepthTarget(VulkanOpenXrDepthTarget target)
    {
        RetireImageResources(new RetiredImageResources(
            target.Image,
            target.Memory,
            target.View,
            [],
            default,
            0));
    }

    private void DestroyOpenXrRenderingResources()
    {
        DestroyOpenXrEyeRecordWorkers();
        DestroyOpenXrPrimaryCommandBufferCache();
        DestroyOpenXrResourcePlannerState();

        foreach (VulkanOpenXrSwapchainImageViewCacheEntry entry in _openXrBackend.SwapchainImageViews.Values)
            if (entry.View.Handle != 0 && TryBeginDestroyImageView(entry.View, "DestroyOpenXrSwapchainImageViewCache"))
                Api!.DestroyImageView(device, entry.View, null);
        
        _openXrBackend.SwapchainImageViews.Clear();

        for (int i = 0; i < _openXrBackend.CachedDepthTargets.Length; i++)
        {
            DestroyOpenXrDepthTarget(_openXrBackend.CachedDepthTargets[i]);
            _openXrBackend.CachedDepthTargets[i] = default;
            _openXrBackend.CachedDepthExtents[i] = default;
        }

    }

    internal void ResetOpenXrRenderingResourcesForRuntimeRecreate(string reason)
    {
        if (_deviceLost || Api is null || device.Handle == 0)
            return;

        Debug.VulkanWarning(
            "[OpenXR] Resetting Vulkan OpenXR render resources before runtime recreate. Reason={0}",
            string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);

        try
        {
            DeviceWaitIdle();
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning(
                "[OpenXR] Device idle wait failed while resetting Vulkan OpenXR resources before runtime recreate. Error={0}",
                ex.Message);
        }

        if (_deviceLost)
            return;

        DestroyOpenXrRenderingResources();
        MarkCommandBuffersDirty(nameof(ResetOpenXrRenderingResourcesForRuntimeRecreate));
    }

    internal void ExecuteOpenXrRuntimeGraphicsTransition(string reason, Action transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        if (_deviceLost || Api is null || device.Handle == 0)
            throw new InvalidOperationException("Cannot initialize OpenXR Vulkan session resources after the Vulkan device was lost.");

        long lockWaitStart = Stopwatch.GetTimestamp();
        bool queueLockTaken = false;
        try
        {
            Monitor.Enter(_oneTimeSubmitLock, ref queueLockTaken);
            LogOpenXrSerializedCriticalSectionWait("RuntimeGraphicsTransition", lockWaitStart, Stopwatch.GetTimestamp());

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RuntimeGraphicsTransition"))
            {
                Debug.Vulkan(
                    "[OpenXR] Beginning Vulkan runtime graphics transition. Reason={0}",
                    string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);

                WaitForAllInFlightWork();
                if (!_deviceLost)
                    DeviceWaitIdle();
                if (_deviceLost)
                    throw new InvalidOperationException("Vulkan device lost while waiting for idle before OpenXR session initialization.");

                transition();

                if (!_deviceLost)
                    DeviceWaitIdle();
                if (_deviceLost)
                    throw new InvalidOperationException("Vulkan device lost while waiting for idle after OpenXR session initialization.");

                Debug.Vulkan(
                    "[OpenXR] Completed Vulkan runtime graphics transition. Reason={0}",
                    string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);
            }
        }
        finally
        {
            if (queueLockTaken)
                Monitor.Exit(_oneTimeSubmitLock);
        }
    }

    internal bool ShouldDeferOpenXrRuntimeSessionStart(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (RuntimeEngine.StartupPresentationEnabled)
        {
            reason = "editor startup presentation is still active";
            return true;
        }

        if (ShouldDeferOpenXrVulkanResourceWork(out string resourceWorkReason))
        {
            reason = resourceWorkReason;
            return true;
        }

        ulong acceptedDesktopFrameAttemptCount = AcceptedDesktopFrameAttemptCount;
        if (acceptedDesktopFrameAttemptCount < MinDesktopFramesBeforeOpenXrRuntimeSessionStart)
        {
            reason = $"desktop renderer has accepted too few startup frame attempts ({acceptedDesktopFrameAttemptCount}/{MinDesktopFramesBeforeOpenXrRuntimeSessionStart})";
            return true;
        }

        if (!HasObservedDesktopFrameTick)
        {
            reason = "desktop renderer has not observed a completed or resize-skipped frame tick yet";
            return true;
        }

        if (CaptureDesktopFrameActivity().IsActive)
        {
            reason = "desktop renderer is currently recording/submitting a frame";
            return true;
        }

        long lastDirtyTimestamp = Volatile.Read(ref _lastCommandBufferDirtyTimestamp);
        if (lastDirtyTimestamp != 0)
        {
            TimeSpan dirtyAge = Stopwatch.GetElapsedTime(lastDirtyTimestamp);
            if (dirtyAge < OpenXrRuntimeSessionStartDirtyQuietPeriod)
            {
                long now = Stopwatch.GetTimestamp();
                long dirtyWaitStart = Volatile.Read(ref _openXrBackend.RuntimeSessionStartDirtyWaitStartTimestamp);
                if (dirtyWaitStart == 0)
                {
                    Interlocked.CompareExchange(ref _openXrBackend.RuntimeSessionStartDirtyWaitStartTimestamp, now, 0);
                    dirtyWaitStart = Volatile.Read(ref _openXrBackend.RuntimeSessionStartDirtyWaitStartTimestamp);
                }

                TimeSpan dirtyWait = Stopwatch.GetElapsedTime(dirtyWaitStart, now);
                if (dirtyWait < OpenXrRuntimeSessionStartDirtyMaxWait)
                {
                    reason =
                        $"desktop command buffers were dirtied {dirtyAge.TotalMilliseconds:F0} ms ago (waiting {dirtyWait.TotalMilliseconds:F0}/{OpenXrRuntimeSessionStartDirtyMaxWait.TotalMilliseconds:F0} ms for a quiet window)";
                    return true;
                }

                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.SessionStartDirtyQuietBypassed.{GetHashCode()}",
                    TimeSpan.FromSeconds(5),
                    "[OpenXR] Proceeding with Vulkan session creation despite desktop command buffers dirtied {0:F0} ms ago after waiting {1:F0} ms. The runtime graphics transition will wait for in-flight work and idle the device.",
                    dirtyAge.TotalMilliseconds,
                    dirtyWait.TotalMilliseconds);
            }
        }

        if (TryGetPendingSubmittedFrameSlot(out int pendingSlot, out ulong pendingTimelineValue))
        {
            long now = Stopwatch.GetTimestamp();
            long pendingFrameWaitStart = Volatile.Read(ref _openXrBackend.RuntimeSessionStartPendingFrameWaitStartTimestamp);
            if (pendingFrameWaitStart == 0)
            {
                Interlocked.CompareExchange(ref _openXrBackend.RuntimeSessionStartPendingFrameWaitStartTimestamp, now, 0);
                pendingFrameWaitStart = Volatile.Read(ref _openXrBackend.RuntimeSessionStartPendingFrameWaitStartTimestamp);
            }

            TimeSpan pendingFrameWait = Stopwatch.GetElapsedTime(pendingFrameWaitStart, now);
            if (pendingFrameWait < OpenXrRuntimeSessionStartPendingFrameMaxWait)
            {
                reason =
                    $"desktop frame slot {pendingSlot} is still pending at timeline value {pendingTimelineValue} (waiting {pendingFrameWait.TotalMilliseconds:F0}/{OpenXrRuntimeSessionStartPendingFrameMaxWait.TotalMilliseconds:F0} ms for submitted desktop work to retire)";
                return true;
            }

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.SessionStartPendingDesktopFrameBypassed.{GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[OpenXR] Proceeding with Vulkan session creation despite desktop frame slot {0} still pending at timeline value {1} after waiting {2:F0} ms. The runtime graphics transition will wait for in-flight work and idle the device.",
                pendingSlot,
                pendingTimelineValue,
                pendingFrameWait.TotalMilliseconds);
        }

        Volatile.Write(ref _openXrBackend.RuntimeSessionStartDirtyWaitStartTimestamp, 0);
        Volatile.Write(ref _openXrBackend.RuntimeSessionStartPendingFrameWaitStartTimestamp, 0);

        return false;
    }

    internal bool ShouldDeferOpenXrEyePreviewCopyWork(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (ImportedTextureStreamingManager.Instance.TryDescribeBlockingOpenXrEyeTextureWork(out string textureWorkReason))
        {
            reason = textureWorkReason;
            return true;
        }

        if (TryDescribeRecentResourceAllocationFailure(out string allocationFailureReason))
        {
            reason = allocationFailureReason;
            return true;
        }

        if (TryDescribeOpenXrVulkanAllocatorPressure(out string allocatorPressureReason))
        {
            reason = allocatorPressureReason;
            return true;
        }

        return false;
    }

    internal bool ShouldDeferOpenXrVulkanResourceWork(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (ImportedTextureStreamingManager.Instance.TryDescribeActiveStartupTextureWork(out string textureWorkReason))
        {
            reason = textureWorkReason;
            return true;
        }

        if (TryDescribeRecentResourceAllocationFailure(out string allocationFailureReason))
        {
            reason = allocationFailureReason;
            return true;
        }

        if (TryDescribeOpenXrVulkanAllocatorPressure(out string allocatorPressureReason))
        {
            reason = allocatorPressureReason;
            return true;
        }

        return false;
    }

    internal bool ShouldDeferOpenXrEyeRenderingWork(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (ImportedTextureStreamingManager.Instance.TryDescribeBlockingOpenXrEyeTextureWork(out string textureWorkReason))
        {
            reason = textureWorkReason;
            return true;
        }

        return false;
    }

    internal bool ShouldDeferTextureUploadPreparationForOpenXrPriority(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        IRuntimeRenderPresentationServices host = RuntimeRenderingHostServices.Presentation;
        if (!host.IsOpenXRActive && !host.IsInVR)
            return false;

        if (TryDescribeRecentResourceAllocationFailure(out string allocationFailureReason))
        {
            reason = allocationFailureReason;
            return true;
        }

        if (TryDescribeOpenXrVulkanAllocatorPressure(out string allocatorPressureReason))
        {
            reason = allocatorPressureReason;
            return true;
        }

        return false;
    }

    internal bool TryGetVulkanAllocatorBudgetSnapshot(
        double budgetRatio,
        long reserveBytes,
        out long allocatedBytes,
        out long budgetBytes,
        out long largestHeapBytes,
        out int activeAllocationCount)
    {
        allocatedBytes = 0L;
        budgetBytes = 0L;
        largestHeapBytes = 0L;
        activeAllocationCount = 0;

        try
        {
            activeAllocationCount = MemoryAllocator.ActiveVkAllocationCount;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (MemoryAllocator is VulkanVmaAllocator vmaAllocator
            && Api is not null
            && _physicalDevice.Handle != 0)
        {
            Api.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
            if (vmaAllocator.TryGetDeviceLocalHeapBudgetSnapshot(
                    in memoryProperties,
                    budgetRatio,
                    reserveBytes,
                    out allocatedBytes,
                    out budgetBytes,
                    out largestHeapBytes))
            {
                return true;
            }
        }

        try
        {
            allocatedBytes = MemoryAllocator.TotalAllocatedBytes;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        largestHeapBytes = ResolveLargestVulkanMemoryHeapBytes();
        if (largestHeapBytes <= 0)
            return false;

        double clampedRatio = Math.Clamp(budgetRatio, 0.1, 1.0);
        long ratioLimitBytes = (long)Math.Floor(largestHeapBytes * clampedRatio);
        long reserveLimitBytes = largestHeapBytes > reserveBytes
            ? largestHeapBytes - Math.Max(0L, reserveBytes)
            : largestHeapBytes;
        budgetBytes = Math.Max(0L, Math.Min(ratioLimitBytes, reserveLimitBytes));
        return budgetBytes > 0L;
    }

    private bool TryDescribeOpenXrVulkanAllocatorPressure(out string reason)
    {
        reason = string.Empty;

        if (!TryGetVulkanAllocatorBudgetSnapshot(
                OpenXrVulkanAllocatorPressureDeferRatio,
                OpenXrVulkanAllocatorPressureReserveBytes,
                out long allocatedBytes,
                out long deferLimitBytes,
                out long largestHeapBytes,
                out int activeAllocationCount))
        {
            return false;
        }

        if (allocatedBytes < deferLimitBytes)
            return false;

        reason =
            $"Vulkan allocator pressure is high (allocated={allocatedBytes}, largestHeap={largestHeapBytes}, deferLimit={deferLimitBytes}, activeVkAllocations={activeAllocationCount})";
        return true;
    }

    private long ResolveLargestVulkanMemoryHeapBytes()
    {
        if (Api is null || _physicalDevice.Handle == 0)
            return 0;

        Api.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
        ulong largestHeapBytes = 0;
        for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
            largestHeapBytes = Math.Max(largestHeapBytes, memoryProperties.MemoryHeaps[i].Size);

        return largestHeapBytes > long.MaxValue
            ? long.MaxValue
            : (long)largestHeapBytes;
    }

    private bool TryGetPendingSubmittedFrameSlot(
        out int pendingSlot,
        out ulong pendingTimelineValue)
    {
        pendingSlot = -1;
        pendingTimelineValue = 0;

        using DesktopFrameRetirementScope retirement =
            EnterDesktopFrameRetirementScope();
        ReadOnlySpan<ulong> timelineValues = retirement.TimelineValues;
        Silk.NET.Vulkan.Semaphore timelineSemaphore =
            retirement.TimelineSemaphore;
        if (timelineValues.IsEmpty || timelineSemaphore.Handle == 0)
            return false;

        int frameSlotCount = Math.Min(
            timelineValues.Length,
            MAX_FRAMES_IN_FLIGHT);
        for (int i = 0; i < frameSlotCount; i++)
        {
            ulong value = timelineValues[i];
            if (value == 0 ||
                HasTimelineValueCompleted(timelineSemaphore, value))
            {
                continue;
            }

            pendingSlot = i;
            pendingTimelineValue = value;
            return true;
        }

        return false;
    }
    private void DestroyOpenXrPrimaryCommandBufferCache()
    {
        lock (_openXrBackend.PrimaryCommandArtifactOwnersLock)
        {
            if (OpenXrPrimaryCommandArtifactOwners.Count != 0)
            {
                foreach (PrimaryCommandArtifactOwner variant in OpenXrPrimaryCommandArtifactOwners.Values)
                {
                    CommandBuffer primary = variant.PrimaryCommandBuffer;
                    if (primary.Handle != 0)
                    {
                        if (variant.OwnsPrimaryCommandBuffer && !_deviceLost)
                        {
                            CommandPool ownerPool = variant.PrimaryCommandPool.Handle != 0
                                ? variant.PrimaryCommandPool
                                : commandPool;
                            FreeVulkanCommandBufferTracked(ownerPool, ref primary, "OpenXR.PrimaryOwner");
                        }
                        RemoveCommandBufferBindState(variant.PrimaryCommandBuffer);
                    }

                    CommandBuffer dynamicSecondary = variant.DynamicUiSecondaryCommandBuffer;
                    if (dynamicSecondary.Handle != 0)
                    {
                        if (variant.OwnsDynamicUiSecondaryCommandBuffer && !_deviceLost)
                        {
                            CommandPool ownerPool = variant.DynamicUiSecondaryCommandPool.Handle != 0
                                ? variant.DynamicUiSecondaryCommandPool
                                : commandPool;
                            FreeVulkanCommandBufferTracked(ownerPool, ref dynamicSecondary, "OpenXR.DynamicSecondaryOwner");
                        }
                        RemoveCommandBufferBindState(variant.DynamicUiSecondaryCommandBuffer);
                    }
                }

                OpenXrPrimaryCommandArtifactOwners.Clear();
            }
        }

        DestroyOpenXrEyeCommandPools();
    }

    private void DestroyOpenXrResourcePlannerState()
    {
        KeyValuePair<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState>[] states;
        lock (_openXrBackend.ResourcePlannerStatesLock)
        {
            if (OpenXrResourcePlannerStates.Count == 0)
                return;

            states = OpenXrResourcePlannerStates.ToArray();
            OpenXrResourcePlannerStates.Clear();
        }

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        HashSet<VulkanResourceAllocator> retiredAllocators = new(ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> pair in states)
        {
            RetireResourcePlannerRuntimeStateAllocators(
                pair.Value,
                retiredAllocators,
                $"OpenXrResourcePlannerStateDestroy.{DescribeOpenXrResourcePlannerContextKey(pair.Key)}");
        }

        if (previousState.ResourceAllocator is not null && previousState.ResourceAllocator.IsRetired)
            previousState = ResourcePlannerRuntimeState.CreateEmpty();
        RestoreResourcePlannerRuntimeState(previousState);
    }

}
