using System.Buffers;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Publishes ownership returned by OpenXR for an acquired runtime image.
    /// The command authority owns this ledger; callers retain only the image and
    /// frozen subresource range needed for the transition.
    /// </summary>
    internal void PublishOpenXrExternalImageAcquireState(
        Image image,
        in ImageSubresourceRange range)
    {
        if (image.Handle == 0)
            throw new InvalidOperationException("An OpenXR acquire cannot publish a null Vulkan image.");

        ulong generation = ResourceRuntime.GetPublishedGeneration(ObjectType.Image, image.Handle);
        using (VulkanFrameLockScope.Enter(
                   CommandBuffers.SubmissionStateGate,
                   EVulkanFrameWaitReason.SubmissionStateLock))
        using (VulkanFrameLockScope.Enter(
                   Synchronization._vulkanImageLayoutLock,
                   EVulkanFrameWaitReason.SynchronizationLock))
        {
            Synchronization._externalImageOwnershipByHandle[image.Handle] =
                (generation, EVulkanExternalImageOwnership.OpenXrRuntimeAcquired);
            uint levels = Math.Max(range.LevelCount, 1u);
            uint layers = Math.Max(range.LayerCount, 1u);
            for (uint levelOffset = 0; levelOffset < levels; levelOffset++)
            for (uint layerOffset = 0; layerOffset < layers; layerOffset++)
            {
                UpdateExternalImageOwnershipNoLock(
                    image.Handle, in range, levelOffset, layerOffset, ImageAspectFlags.ColorBit);
                UpdateExternalImageOwnershipNoLock(
                    image.Handle, in range, levelOffset, layerOffset, ImageAspectFlags.DepthBit);
                UpdateExternalImageOwnershipNoLock(
                    image.Handle, in range, levelOffset, layerOffset, ImageAspectFlags.StencilBit);
            }
        }
    }

    private void UpdateExternalImageOwnershipNoLock(
        ulong imageHandle,
        in ImageSubresourceRange range,
        uint levelOffset,
        uint layerOffset,
        ImageAspectFlags aspect)
    {
        if ((range.AspectMask & aspect) == 0)
            return;

        VulkanTrackedImageSubresource key = new(
            imageHandle,
            range.BaseMipLevel + levelOffset,
            range.BaseArrayLayer + layerOffset,
            aspect);
        if (!Synchronization._trackedImageSubresourceStates.TryGetValue(
                key,
                out VulkanImageSubresourceState? state))
        {
            return;
        }

        state.Submitted = state.Submitted with
        {
            ExternalOwnership = EVulkanExternalImageOwnership.OpenXrRuntimeAcquired,
        };
        state.SubmittedVersion = NextSubmittedImageStateVersion(
            state.SubmittedVersion);
        state.Completed = state.Completed with
        {
            ExternalOwnership = EVulkanExternalImageOwnership.OpenXrRuntimeAcquired,
        };
    }

    private static ulong NextSubmittedImageStateVersion(ulong version)
    {
        unchecked
        {
            ++version;
        }
        return version == 0u ? 1u : version;
    }

    /// <summary>
    /// Publishes the Vulkan-defined initial state for every subresource of a newly
    /// created engine-owned image. This closes the gap between native image creation
    /// and the first recorded transition so persistent command buffers can capture a
    /// complete entry-state contract from their first recording.
    /// </summary>
    internal void RegisterTrackedImageInitialLayouts(
        Image image,
        in ImageCreateInfo createInfo)
    {
        ulong imageHandle = image.Handle;
        if (imageHandle == 0)
            return;

        ImageAspectFlags aspectMask = NormalizeBarrierAspectMask(
            createInfo.Format,
            ImageAspectFlags.None);
        ulong resourceGeneration = ResourceRuntime.GetPublishedGeneration(
            ObjectType.Image,
            imageHandle);
        VulkanImageAccessState initialState = ResolveCommandImageAccessState(
            createInfo.InitialLayout,
            aspectMask,
            generation: resourceGeneration);
        uint mipLevels = Math.Max(createInfo.MipLevels, 1u);
        uint arrayLayers = Math.Max(createInfo.ArrayLayers, 1u);

        lock (Synchronization._vulkanImageLayoutLock)
        {
            ClearTrackedImageLayoutsNoLock(imageHandle);
            for (uint mip = 0; mip < mipLevels; mip++)
            for (uint layer = 0; layer < arrayLayers; layer++)
            {
                RegisterAspect(ImageAspectFlags.ColorBit);
                RegisterAspect(ImageAspectFlags.DepthBit);
                RegisterAspect(ImageAspectFlags.StencilBit);

                void RegisterAspect(ImageAspectFlags aspect)
                {
                    if ((aspectMask & aspect) == 0)
                        return;

                    VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, aspect);
                    VulkanImageSubresourceState state = new()
                    {
                        Submitted = initialState,
                        Completed = initialState,
                    };
                    Synchronization._trackedImageSubresourceStates[key] = state;
                    _ = Synchronization.PublishStableImageSubresourceNoLock(state);
                }
            }
        }
    }

    /// <summary>
    /// Resets the command-buffer-local image journal for a new recording generation
    /// while retaining its tables. Command recording owns this state; resource
    /// retirement only requests publication through the command authority.
    /// </summary>
    internal void ResetCommandBufferImageLayoutJournal(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        lock (Synchronization._vulkanImageLayoutLock)
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    handle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState();
                Synchronization._recordedImageLayoutsByCommandBuffer[handle] = recorded;
            }

            recorded.Subresources.Clear();
            recorded.EntrySubresources.Clear();
            recorded.SecondaryDescriptorRequirements.Clear();
            recorded.SecondaryDescriptorImagePayloadGenerations.Clear();
            recorded.TouchedSubresources.Clear();
            recorded.QueueOwnershipTransfers.Clear();
            recorded.EntryStateIncomplete = false;
            recorded.EntryStateFailure = default;
            recorded.RecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer);
        }
    }

    /// <summary>
    /// Closes a command buffer's deferred dependency and image-layout publication
    /// window before a resource enters retirement. This is deliberately a single
    /// runtime operation: a failed dependency publication invalidates only the
    /// affected recording, while a successful publication also commits the image
    /// access deltas that were accumulated by that recording.
    /// </summary>
    internal bool TryFlushTrackingBatchForRetirement(
        VulkanResourceRuntime resources,
        CommandBuffer commandBuffer,
        VulkanCommandBufferTrackingBatch batch,
        VulkanFrameTelemetry telemetry,
        out string failureReason)
    {
        if (!resources.TryPublishCommandBufferTrackingBatch(
                commandBuffer,
                batch,
                out failureReason,
                out long lifetimeLockWaitTicks))
            return false;

        long layoutLockWaitTicks =
            FlushImageAccessBatch(commandBuffer, batch, telemetry);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingContention(
            lifetimeLockContentions:
                lifetimeLockWaitTicks > 0L ? 1 : 0,
            layoutLockContentions:
                layoutLockWaitTicks > 0L ? 1 : 0);
        return true;
    }

    private long FlushImageAccessBatch(
        CommandBuffer commandBuffer,
        VulkanCommandBufferTrackingBatch batch,
        VulkanFrameTelemetry telemetry)
    {
        if (commandBuffer.Handle == 0 ||
            (batch.PublishedImageDeltaCount >= batch.ImageAccessDeltas.Count &&
             batch.PublishedQueueOwnershipTransferCount >= batch.QueueOwnershipTransfers.Count))
        {
            return 0L;
        }

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        long lockWaitTicks = EnterImageLayoutLockMeasured();
        try
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = batch.RecordingGeneration,
                };
                Synchronization._recordedImageLayoutsByCommandBuffer[commandBufferHandle] = recorded;
            }

            for (int index = batch.PublishedImageDeltaCount; index < batch.ImageAccessDeltas.Count; index++)
            {
                VulkanImageAccessRangeDelta delta = batch.ImageAccessDeltas[index];
                ImageSubresourceRange range = delta.Range;
                uint levelCount = Math.Max(range.LevelCount, 1u);
                uint layerCount = Math.Max(range.LayerCount, 1u);
                for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint mip = range.BaseMipLevel + mipOffset;
                    uint layer = range.BaseArrayLayer + layerOffset;
                    RecordImageAspectState(recorded, delta.ImageHandle, mip, layer, range.AspectMask,
                        ImageAspectFlags.ColorBit, delta.State, telemetry);
                    RecordImageAspectState(recorded, delta.ImageHandle, mip, layer, range.AspectMask,
                        ImageAspectFlags.DepthBit, delta.State, telemetry);
                    RecordImageAspectState(recorded, delta.ImageHandle, mip, layer, range.AspectMask,
                        ImageAspectFlags.StencilBit, delta.State, telemetry);
                }
            }

            for (int index = batch.PublishedQueueOwnershipTransferCount;
                 index < batch.QueueOwnershipTransfers.Count;
                 index++)
            {
                recorded.QueueOwnershipTransfers.Add(batch.QueueOwnershipTransfers[index]);
            }
            recorded.RefreshTouchedSubresources();
        }
        finally
        {
            Monitor.Exit(Synchronization._vulkanImageLayoutLock);
        }

        batch.PublishedImageDeltaCount = batch.ImageAccessDeltas.Count;
        batch.PublishedQueueOwnershipTransferCount = batch.QueueOwnershipTransfers.Count;
        return lockWaitTicks;
    }

    private long EnterImageLayoutLockMeasured()
    {
        long waitTicks = 0L;
        if (!Monitor.TryEnter(Synchronization._vulkanImageLayoutLock))
        {
            long waitStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            Monitor.Enter(Synchronization._vulkanImageLayoutLock);
            waitTicks = Math.Max(
                1L,
                System.Diagnostics.Stopwatch.GetTimestamp() - waitStarted);
        }

        VulkanFrameHotPathTelemetry.RecordLayoutLockWait(waitTicks);
        return waitTicks;
    }

    private void RecordImageAspectState(
        VulkanRecordedImageLayoutState recorded,
        ulong imageHandle,
        uint mip,
        uint layer,
        ImageAspectFlags rangeAspect,
        ImageAspectFlags trackedAspect,
        VulkanImageAccessState state,
        VulkanFrameTelemetry telemetry)
    {
        if ((rangeAspect & trackedAspect) == 0)
            return;

        VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
        if (!recorded.Subresources.ContainsKey(key) && !recorded.EntrySubresources.ContainsKey(key))
        {
            if (Synchronization._trackedImageSubresourceStates.TryGetValue(key, out VulkanImageSubresourceState? entrySubmitted))
                recorded.EntrySubresources[key] = entrySubmitted!.Submitted;
            else
            {
                recorded.EntryStateIncomplete = true;
                if (!recorded.EntryStateFailure.RequiresRecording)
                    recorded.EntryStateFailure = new VulkanImageEntryStateMismatch(
                        EVulkanPrimaryEntryStateMismatch.MissingSubmittedState,
                        imageHandle, mip, layer, trackedAspect,
                        VulkanImageAccessState.Undefined,
                        VulkanImageAccessState.Undefined);
            }
        }

        uint queueFamily = state.QueueFamilyIndex;
        if (queueFamily == Vk.QueueFamilyIgnored)
        {
            if (recorded.Subresources.TryGetValue(key, out VulkanImageAccessState prior))
                queueFamily = prior.QueueFamilyIndex;
            else if (Synchronization._trackedImageSubresourceStates.TryGetValue(key, out VulkanImageSubresourceState? submittedState))
                queueFamily = submittedState!.Submitted.QueueFamilyIndex;
        }

        EVulkanExternalImageOwnership ownership = recorded.Subresources.TryGetValue(key, out VulkanImageAccessState existing)
            ? existing.ExternalOwnership
            : Synchronization._trackedImageSubresourceStates.TryGetValue(key, out VulkanImageSubresourceState? submitted)
                ? submitted!.Submitted.ExternalOwnership
                : Synchronization._externalImageOwnershipByHandle.TryGetValue(imageHandle, out var external) &&
                  (external.ResourceGeneration == 0 || state.ResourceGeneration == 0 || external.ResourceGeneration == state.ResourceGeneration)
                    ? external.Ownership
                    : EVulkanExternalImageOwnership.EngineOwned;
        recorded.Subresources[key] = state with
        {
            QueueFamilyIndex = queueFamily,
            Serial = unchecked((ulong)Interlocked.Increment(ref telemetry._vulkanImageLayoutTransitionSerial)),
            ExternalOwnership = ownership,
        };
    }

    /// <summary>
    /// Removes synchronization state for an image which has been detached from
    /// an output generation.  This is output-owned cleanup, not a renderer
    /// facade concern.
    /// </summary>
    internal void ClearTrackedImageLayouts(Image image)
    {
        ulong imageHandle = image.Handle;
        if (imageHandle == 0)
            return;

        lock (Synchronization._vulkanImageLayoutLock)
            ClearTrackedImageLayoutsNoLock(imageHandle);
    }

    /// <summary>
    /// Restores resource-planner layout hints from the submitted command-authority
    /// ledger before a new recording begins. Recording mutates physical-group layout
    /// hints speculatively, so an unsubmitted or rejected attempt must not become the
    /// old-layout authority for the next frame.
    /// </summary>
    internal int ReconcileResourcePlannerImageLayouts(
        VulkanResourceAllocator? allocator)
    {
        if (allocator is null || allocator.IsRetired)
            return 0;

        int reconciledGroupCount = 0;
        foreach (VulkanPhysicalImageGroup group in allocator.EnumeratePhysicalGroups())
        {
            if (!group.IsAllocated || group.Image.Handle == 0)
                continue;

            uint mipLevels = Math.Max(group.MipLevels, 1u);
            uint arrayLayers = Math.Max(group.Template.Layers, 1u);
            ImageAspectFlags aspectMask = NormalizeBarrierAspectMask(
                group.Format,
                ImageAspectFlags.None);
            ImageSubresourceRange wholeRange = new()
            {
                AspectMask = aspectMask,
                BaseMipLevel = 0,
                LevelCount = mipLevels,
                BaseArrayLayer = 0,
                LayerCount = arrayLayers,
            };
            if (Synchronization.TryGetSubmittedImageLayout(
                    group.Image,
                    in wholeRange,
                    out ImageLayout wholeLayout))
            {
                group.LastKnownLayout = wholeLayout;
                reconciledGroupCount++;
                continue;
            }

            // Mip chains can legitimately end a submission in mixed layouts. Only
            // replace the speculative snapshot when every native subresource has a
            // submitted state; otherwise retain the prior hint and let recording
            // defer rather than inventing a partial authority.
            bool complete = true;
            for (uint mipLevel = 0; mipLevel < mipLevels && complete; mipLevel++)
            for (uint arrayLayer = 0; arrayLayer < arrayLayers; arrayLayer++)
            {
                ImageSubresourceRange subresourceRange = new()
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = mipLevel,
                    LevelCount = 1,
                    BaseArrayLayer = arrayLayer,
                    LayerCount = 1,
                };
                if (!Synchronization.TryGetSubmittedImageLayout(
                        group.Image,
                        in subresourceRange,
                        out _))
                {
                    complete = false;
                    break;
                }
            }

            if (!complete)
                continue;

            for (uint mipLevel = 0; mipLevel < mipLevels; mipLevel++)
            for (uint arrayLayer = 0; arrayLayer < arrayLayers; arrayLayer++)
            {
                ImageSubresourceRange subresourceRange = new()
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = mipLevel,
                    LevelCount = 1,
                    BaseArrayLayer = arrayLayer,
                    LayerCount = 1,
                };
                _ = Synchronization.TryGetSubmittedImageLayout(
                    group.Image,
                    in subresourceRange,
                    out ImageLayout subresourceLayout);
                group.UpdateKnownLayout(
                    subresourceLayout,
                    mipLevel,
                    1,
                    arrayLayer,
                    1);
            }

            reconciledGroupCount++;
        }

        return reconciledGroupCount;
    }

    private void ClearTrackedImageLayoutsNoLock(ulong imageHandle)
    {
        RetireTrackedImageSlotsNoLock(imageHandle);
        RemoveImageKeys(Synchronization._trackedImageSubresourceStates, imageHandle);
        Synchronization._externalImageOwnershipByHandle.Remove(imageHandle);
        foreach (VulkanRecordedImageLayoutState recorded in Synchronization._recordedImageLayoutsByCommandBuffer.Values)
        {
            RemoveImageKeys(recorded.EntrySubresources, imageHandle);
            RemoveImageKeys(recorded.SecondaryDescriptorRequirements, imageHandle);
            RemoveImageKeys(recorded.Subresources, imageHandle);
            recorded.RefreshTouchedSubresources();
        }
    }

    private void RetireTrackedImageSlotsNoLock(ulong imageHandle)
    {
        foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageSubresourceState> pair in
                 Synchronization._trackedImageSubresourceStates)
        {
            if (pair.Key.ImageHandle == imageHandle)
                Synchronization.RetireStableImageSubresourceNoLock(pair.Value);
        }
    }

    private static void RemoveImageKeys<TValue>(
        Dictionary<VulkanTrackedImageSubresource, TValue> states,
        ulong imageHandle)
    {
        if (states.Count == 0)
            return;

        VulkanTrackedImageSubresource[] keys = ArrayPool<VulkanTrackedImageSubresource>.Shared.Rent(states.Count);
        int count = 0;
        try
        {
            foreach (VulkanTrackedImageSubresource key in states.Keys)
                if (key.ImageHandle == imageHandle)
                    keys[count++] = key;

            for (int i = 0; i < count; i++)
                states.Remove(keys[i]);
        }
        finally
        {
            ArrayPool<VulkanTrackedImageSubresource>.Shared.Return(keys, clearArray: true);
        }
    }
}
