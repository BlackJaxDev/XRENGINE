using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Buffers;
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
    internal bool TryClearOpenXrSwapchainImage(
        Image image,
        Extent2D extent,
        ColorF4 color,
        in OpenXrImageSubmissionIdentity submissionIdentity)
    {
        if (image.Handle == 0 || extent.Width == 0 || extent.Height == 0)
            return false;

        try
        {
            return _commandRuntime.ExecuteOpenXrDiagnosticClear(
                image,
                extent,
                color,
                in submissionIdentity);
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
            _commandRuntime.TryGetTrackedImageLayout(source.DescriptorImage, range, out ImageLayout liveLayout) &&
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

        return _commandRuntime.TryGetTrackedImageLayout(image, colorRange, out ImageLayout trackedLayout) &&
            trackedLayout != ImageLayout.Undefined
                ? trackedLayout
                : ImageLayout.ColorAttachmentOptimal;
    }

    private static ImageAspectFlags NormalizeOpenXrMirrorAspect(Format format, ImageAspectFlags aspect)
    {
        if (!VulkanDesktopSwapchainService.IsDepthStencilFormatForOutput(format))
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
        => _commandRuntime.TransitionOpenXrMirrorImage(
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
        => _commandRuntime.TransitionOpenXrMirrorImage(
            commandBuffer,
            image,
            format,
            oldLayout,
            newLayout,
            aspectMask,
            baseArrayLayer,
            layerCount);

    private void DrainRetiredResourcesFromCompletedSubmittedFrameSlots()
    {
        ResourceRuntime.Descriptors.DrainReleasedMaterialDescriptorClosures();
        _commandRuntime.DrainRetiredSynchronousSubmissions();
        using VulkanDesktopFrameRetirementScope retirement =
            new(_commandRuntime, RetirementGate);
        ReadOnlySpan<ulong> timelineValues = retirement.TimelineValues;
        if (timelineValues.IsEmpty)
        {
            ResourceRuntime.Uploads.DrainCompletedRecordedTextureUploadPublications(
                Api!, _deviceContext, _commandRuntime, ResourceRuntime, IsDeviceLost);
            return;
        }

        int frameSlotCount = Math.Min(
            timelineValues.Length,
            FrameSlotCount);
        DesktopFrameActivitySnapshot desktopActivity =
            CaptureDesktopFrameActivity();
        bool[] rentedDrainableSlots = ArrayPool<bool>.Shared.Rent(frameSlotCount);
        Span<bool> drainableSlots = rentedDrainableSlots.AsSpan(0, frameSlotCount);
        drainableSlots.Clear();
        try
        {
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
                    ResourceRuntime.ResidentTemplateFrameSlotLifetimes
                        .ReleaseFrameSlot(i);

        const int retirementBudgetPerType = 32;
        for (int i = 0; i < frameSlotCount; i++)
                if (drainableSlots[i])
                    DrainRetiredCommandBuffers(i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredCommandPools(i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredDescriptorSets(i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredDescriptorPools(i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredPipelines(i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                ResourceRuntime.DrainRetiredPipelineLayouts(Api!, _deviceContext.Device, i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                ResourceRuntime.DrainRetiredDescriptorSetLayouts(Api!, _deviceContext.Device, i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredQueryPools(i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredBufferViews(i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredFramebuffers(i, retirementBudgetPerType);
            for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i])
                DrainRetiredBuffers(i, retirementBudgetPerType);
            for (int pass = 0; pass < frameSlotCount; pass++)
            for (int i = 0; i < frameSlotCount; i++)
                if (drainableSlots[i])
                    ResourceRuntime.DrainRetiredImages(
                        Api!,
                        _deviceContext.Device,
                        i,
                        retirementBudgetPerType);

            ResourceRuntime.Uploads.DrainCompletedRecordedTextureUploadPublications(
                Api!, _deviceContext, _commandRuntime, ResourceRuntime, IsDeviceLost);
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(rentedDrainableSlots);
        }
    }
    private bool TryPrepareOpenXrFrameDataSlot(
        uint frameDataImageIndex,
        string reason,
        out bool completionProven)
    {
        completionProven = false;
        OpenXrVulkanSubmissionTracker tracker = _commandRuntime.OpenXrSubmissionTracker;
        if (tracker.OwnsRegisteredFrameDataSlot(
                frameDataImageIndex,
                _commandRuntime.MappedFrameArena,
                _commandRuntime.MappedFrameArena?.Generation ?? 0UL,
                _commandRuntime.ResourceRuntime.FrameDataArena,
                _commandRuntime.ResourceRuntime.FrameDataArena?.Generation ?? 0UL))
        {
            tracker.NotifyRegisteredFrameDataSlotPressure();
            return false;
        }
        ulong value;
        Silk.NET.Vulkan.Semaphore timelineSemaphore;
        using (VulkanDesktopFrameRetirementScope retirement =
               new(_commandRuntime, RetirementGate))
        {
            ReadOnlySpan<ulong> timelineValues = retirement.TimelineValues;
            timelineSemaphore = retirement.TimelineSemaphore;
            if (timelineSemaphore.Handle == 0 ||
                frameDataImageIndex >= timelineValues.Length)
            {
                return true;
            }

            value = timelineValues[(int)frameDataImageIndex];
            if (value == 0)
                return true;
            if (HasTimelineValueCompleted(timelineSemaphore, value))
            {
                completionProven = true;
                return true;
            }
        }

        Debug.VulkanWarningEvery(
            $"OpenXR.Vulkan.DeferFrameDataSlot.{GetHashCode()}.{frameDataImageIndex}.{reason}",
            TimeSpan.FromSeconds(1),
            "[OpenXR] Deferring {1}: frame-data slot {0} still owns pending timeline value {2}.",
            frameDataImageIndex,
            reason,
            value);
        return false;
    }


    private VulkanOpenXrDepthTarget GetOrCreateOpenXrDepthTarget(uint openXrViewIndex, Extent2D extent)
    {
        int targetIndex = ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex);
        return OpenXrOutputResourceService
            .GetOrCreateDepthTarget(targetIndex, extent);
    }

    private ImageView GetOrCreateOpenXrSwapchainImageView(Image image, Format format)
        => OpenXrOutputResourceService.GetOrCreateSwapchainImageView(image, format);





    internal void DestroyOpenXrRenderingResources()
    {
        DestroyOpenXrEyeRecordWorkers();
        DestroyOpenXrPrimaryCommandBufferCache();
        DestroyOpenXrResourcePlannerState();

        OpenXrOutputResourceService.RetireResources();

    }

    /// <summary>
    /// Detaches command-side OpenXR artifacts and queues only children backed by
    /// the retiring runtime images. Caller holds DeviceQueueAdmissionGate's
    /// write lock, so no new recording can retain the old generation.
    /// </summary>
    internal VulkanOpenXrSwapchainChildRetirementReceipt RetireOpenXrSwapchainChildren(
        ReadOnlySpan<Image> retiringImages)
    {
        DestroyOpenXrEyeRecordWorkers();
        DestroyOpenXrPrimaryCommandBufferCache();
        DestroyOpenXrResourcePlannerState();
        _commandRuntime.MarkCommandBuffersDirty(nameof(RetireOpenXrSwapchainChildren));
        return OpenXrOutputResourceService.RetireSwapchainChildren(retiringImages);
    }

    /// <summary>
    /// Advances every normal lifetime queue independently of desktop output.
    /// OpenXR teardown can run after desktop submission has stopped.
    /// </summary>
    internal void DrainOpenXrRetiredDependencies()
    {
        const int retirementBudgetPerType = 32;
        ResourceRuntime.Descriptors.DrainReleasedMaterialDescriptorClosures();
        _commandRuntime.DrainRetiredSynchronousSubmissions();
        int slots = _commandRuntime.ResourceRuntime.Lifetime.Retirement.CommandBuffers.Length;
        for (int slot = 0; slot < slots; slot++)
            DrainRetiredCommandBuffers(slot, retirementBudgetPerType);
        for (int slot = 0; slot < slots; slot++)
            DrainRetiredCommandPools(slot, retirementBudgetPerType);
        for (int slot = 0; slot < slots; slot++)
            DrainRetiredDescriptorSets(slot, retirementBudgetPerType);
        for (int slot = 0; slot < slots; slot++)
            DrainRetiredDescriptorPools(slot, retirementBudgetPerType);
        for (int slot = 0; slot < slots; slot++)
            DrainRetiredFramebuffers(slot, retirementBudgetPerType);
        for (int slot = 0; slot < slots; slot++)
            _commandRuntime.ResourceRuntime.DrainRetiredImages(
                Api!, _deviceContext.Device, slot, retirementBudgetPerType);
    }

    internal void ResetOpenXrRenderingResourcesForRuntimeRecreate(string reason)
    {
        if (_deviceLost || Api is null || _deviceContext.Device.Handle == 0)
            return;

        Debug.VulkanWarning(
            "[OpenXR] Resetting Vulkan OpenXR render resources before runtime recreate. Reason={0}",
            string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);

        // The admission gate stops new OpenXR submission while cached command
        // artifacts detach. Every detached native object enters its exact
        // submission-backed retirement queue, so resolution replacement never
        // requires a device-wide idle wait. Terminal session initialization and
        // device-loss paths retain their explicit synchronization elsewhere.
        _commandRuntime.CommandBuffers.DeviceQueueAdmissionGate.EnterWriteLock();
        try
        {
            if (_deviceLost)
                return;

            DestroyOpenXrRenderingResources();
            _commandRuntime.MarkCommandBuffersDirty(nameof(ResetOpenXrRenderingResourcesForRuntimeRecreate));
        }
        finally
        {
            _commandRuntime.CommandBuffers.DeviceQueueAdmissionGate.ExitWriteLock();
        }
    }

    internal void ExecuteOpenXrRuntimeGraphicsTransition(string reason, Action transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        if (_deviceLost || Api is null || _deviceContext.Device.Handle == 0)
            throw new InvalidOperationException("Cannot initialize OpenXR Vulkan session resources after the Vulkan device was lost.");

        using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RuntimeGraphicsTransition"))
        {
            Debug.Vulkan(
                "[OpenXR] Beginning Vulkan runtime graphics transition. Reason={0}",
                string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);

            _commandRuntime.CommandBuffers.DeviceQueueAdmissionGate.EnterWriteLock();
            try
            {
                // Exclusive device admission prevents new submits/presents while
                // completion is proven. It is not the native queue gate, so no
                // queue mutex is held across timeline or device waits.
                WaitForAllInFlightWork();
                if (!_deviceLost)
                    DeviceWaitIdle();
                if (_deviceLost)
                    throw new InvalidOperationException("Vulkan device lost while waiting for idle before OpenXR session initialization.");

                // Runtime session/reference-space/swapchain setup is protected
                // by exclusive device admission, not by the native queue mutex.
                transition();

                if (!_deviceLost)
                    DeviceWaitIdle();
                if (_deviceLost)
                    throw new InvalidOperationException("Vulkan device lost while waiting for idle after OpenXR session initialization.");
            }
            finally
            {
                _commandRuntime.CommandBuffers.DeviceQueueAdmissionGate.ExitWriteLock();
            }

            Debug.Vulkan(
                "[OpenXR] Completed Vulkan runtime graphics transition. Reason={0}",
                string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);
        }
    }

    internal bool ShouldDeferOpenXrRuntimeSessionStart(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || _deviceContext.Device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (RuntimeEngine.StartupPresentationEnabled)
        {
            reason = "editor startup presentation is still active";
            return true;
        }

        if (CaptureDesktopFrameActivity().IsActive)
        {
            reason = "desktop renderer is currently recording/submitting a frame";
            return true;
        }

        // Session creation runs on the render owner between desktop frames. The
        // graphics transition excludes queue admission and proves GPU completion;
        // unrelated asset queues and command-buffer churn need not become idle.
        return false;
    }

    internal bool ShouldDeferOpenXrEyePreviewCopyWork(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || _deviceContext.Device.Handle == 0)
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

        if (_deviceLost || Api is null || _deviceContext.Device.Handle == 0)
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

        if (_deviceLost || Api is null || _deviceContext.Device.Handle == 0)
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

        if (_deviceLost || Api is null || _deviceContext.Device.Handle == 0)
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
        return Api is not null && _resourceRuntime.TryGetAllocatorBudgetSnapshot(
            Api,
            _deviceContext,
            budgetRatio,
            reserveBytes,
            out allocatedBytes,
            out budgetBytes,
            out largestHeapBytes,
            out activeAllocationCount);
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
        if (Api is null || _deviceContext.PhysicalDevice.Handle == 0)
            return 0;

        Api.GetPhysicalDeviceMemoryProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
        ulong largestHeapBytes = 0;
        for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
            largestHeapBytes = Math.Max(largestHeapBytes, memoryProperties.MemoryHeaps[i].Size);

        return largestHeapBytes > long.MaxValue
            ? long.MaxValue
            : (long)largestHeapBytes;
    }

    private void DestroyOpenXrPrimaryCommandBufferCache()
        => _commandRuntime.DestroyOpenXrPrimaryCommandArtifacts();

    private void DestroyOpenXrResourcePlannerState()
    {
        KeyValuePair<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState>[] states;
        lock (OutputRuntime.OpenXrBackend.ResourcePlannerStatesLock)
        {
            _lastSubmittedOpenXrStereoMirrorContextId = 0UL;
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
