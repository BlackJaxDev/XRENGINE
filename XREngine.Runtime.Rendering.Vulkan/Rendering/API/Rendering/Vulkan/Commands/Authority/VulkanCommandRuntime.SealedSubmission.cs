using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private readonly SealedSubmissionContract?[] _sealedSubmissionContractScratch =
        new SealedSubmissionContract?[16];
    private readonly VulkanSealedImageExitState[] _sealedSubmissionImageOverlayScratch =
        new VulkanSealedImageExitState[256];

    private unsafe bool TryAcquireSealedSubmissionPins(
        Queue queue,
        ref SubmitInfo submitInfo,
        out string failureReason,
        out RuntimeEngine.Rendering.Stats.Vulkan.SealedSubmissionFallbackReason fallbackReason)
    {
        failureReason = string.Empty;
        fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
            .SealedSubmissionFallbackReason.Unknown;
        int commandCount = checked((int)submitInfo.CommandBufferCount);
        if (commandCount == 0 ||
            commandCount > _sealedSubmissionContractScratch.Length ||
            submitInfo.PCommandBuffers is null)
        {
            failureReason = "sealed fast path command-buffer batch shape is unsupported";
            fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                .SealedSubmissionFallbackReason.Shape;
            return false;
        }

        uint queueFamilyIndex = ResolveQueueFamilyIndex(queue);
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;

        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                ulong handle = unchecked(
                    (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                for (int priorIndex = 0; priorIndex < commandIndex; ++priorIndex)
                {
                    if (submitInfo.PCommandBuffers[priorIndex].Handle ==
                        submitInfo.PCommandBuffers[commandIndex].Handle)
                    {
                        ClearSealedSubmissionContractScratch(commandIndex);
                        failureReason = "sealed batch contains a duplicate command buffer";
                        fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                            .SealedSubmissionFallbackReason.Shape;
                        return false;
                    }
                }

                EVulkanSealedResourceMatch resourceMatch =
                    EVulkanSealedResourceMatch.CommandBuffer;
                VulkanResourceLifetimeKey mismatchKey = default;
                if (handle == 0)
                {
                    ClearSealedSubmissionContractScratch(commandIndex);
                    failureReason = "sealed batch contains a null command buffer";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.Shape;
                    return false;
                }
                if (!tracker.CommandBufferLifetimes.TryGetValue(
                        handle,
                        out VulkanCommandBufferLifetimeRecord? lifetime) ||
                    lifetime.SealedSubmissionContract is not { } candidate)
                {
                    ClearSealedSubmissionContractScratch(commandIndex);
                    failureReason = "command buffer has no sealed submission contract";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.MissingContract;
                    return false;
                }
                if (candidate.CommandBufferHandle != handle ||
                    candidate.QueueFamilyIndex != queueFamilyIndex)
                {
                    ClearSealedSubmissionContractScratch(commandIndex);
                    failureReason = "sealed command buffer or queue family changed";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.ResourceVector;
                    return false;
                }
                if (lifetime.QueuedSubmissionCount != 0)
                {
                    ClearSealedSubmissionContractScratch(commandIndex);
                    failureReason =
                        $"sealed command buffer still has {lifetime.QueuedSubmissionCount} queued submissions";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.ResourceVector;
                    return false;
                }
                resourceMatch = candidate.MatchResourceVectorNoLock(
                        tracker,
                        lifetime,
                        out mismatchKey);
                if (resourceMatch != EVulkanSealedResourceMatch.Match)
                {
                    ClearSealedSubmissionContractScratch(commandIndex);
                    failureReason =
                        $"sealed resource generation vector changed at {mismatchKey}";
                    fallbackReason = resourceMatch ==
                        EVulkanSealedResourceMatch.DescriptorPublication
                            ? RuntimeEngine.Rendering.Stats.Vulkan
                                .SealedSubmissionFallbackReason.DescriptorVector
                            : RuntimeEngine.Rendering.Stats.Vulkan
                                .SealedSubmissionFallbackReason.ResourceVector;
                    return false;
                }
                _sealedSubmissionContractScratch[commandIndex] = candidate;

                if (CommandBuffers.TrackingBatches.TryGetValue(
                        handle,
                        out VulkanCommandBufferTrackingBatch? trackingBatch))
                {
                    using (VulkanFrameLockScope.Enter(
                               trackingBatch,
                               EVulkanFrameWaitReason.ResourceLifetimeLock))
                    {
                        if (trackingBatch.IsRecording ||
                            trackingBatch.QueuedSubmissionCount != 0 ||
                            trackingBatch.Dependencies.Count != 0 ||
                            trackingBatch.PublishedImageDeltaCount !=
                                trackingBatch.ImageAccessDeltas.Count ||
                            trackingBatch.PublishedQueueOwnershipTransferCount !=
                                trackingBatch.QueueOwnershipTransfers.Count)
                        {
                            ClearSealedSubmissionContractScratch(commandIndex + 1);
                            failureReason = "sealed command tracking batch is dirty";
                            fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                                .SealedSubmissionFallbackReason.TrackingBatch;
                            return false;
                        }
                    }
                }
            }
        }

        using (VulkanFrameLockScope.Enter(
                   Synchronization._vulkanImageLayoutLock,
                   EVulkanFrameWaitReason.SynchronizationLock))
        {
            int overlayCount = 0;
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                ulong handle = unchecked(
                    (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                SealedSubmissionContract contract =
                    _sealedSubmissionContractScratch[commandIndex]!;
                if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                        handle,
                        out VulkanRecordedImageLayoutState? recorded) ||
                    recorded.RecordingGeneration != contract.ImageRecordingGeneration ||
                    recorded.EntryStateIncomplete ||
                    recorded.QueueOwnershipTransfers.Count != 0 ||
                    !TryMatchSealedImageEntriesNoLock(contract, ref overlayCount) ||
                    !TryAppendSealedImageExits(contract, ref overlayCount))
                {
                    ClearSealedSubmissionContractScratch(commandCount);
                    failureReason = "sealed image generation vector changed";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.ImageVector;
                    return false;
                }
            }
        }

        // Revalidate and pin transactionally after image validation. The
        // submission-state gate prevents another submit from crossing this
        // boundary; the lifetime lock protects retirement and pin counts.
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                ulong handle = unchecked(
                    (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                SealedSubmissionContract contract =
                    _sealedSubmissionContractScratch[commandIndex]!;
                if (!tracker.CommandBufferLifetimes.TryGetValue(
                        handle,
                        out VulkanCommandBufferLifetimeRecord? lifetime) ||
                    !ReferenceEquals(lifetime.SealedSubmissionContract, contract) ||
                    lifetime.QueuedSubmissionCount != 0 ||
                    contract.MatchResourceVectorNoLock(
                        tracker,
                        lifetime,
                        out _) !=
                        EVulkanSealedResourceMatch.Match)
                {
                    ClearSealedSubmissionContractScratch(commandCount);
                    failureReason = "sealed resource vector changed before pin commit";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.PinCommit;
                    return false;
                }
            }

            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                ulong handle = unchecked(
                    (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                VulkanCommandBufferLifetimeRecord lifetime =
                    tracker.CommandBufferLifetimes[handle];
                SealedSubmissionContract contract =
                    _sealedSubmissionContractScratch[commandIndex]!;
                if (lifetime.SubmissionPinReceipt.TryCapture(
                        contract.CommandBufferSlot,
                        contract.Resources))
                {
                    continue;
                }

                for (int priorIndex = 0;
                     priorIndex < commandIndex;
                     ++priorIndex)
                {
                    ulong priorHandle = unchecked(
                        (ulong)submitInfo.PCommandBuffers[priorIndex].Handle);
                    tracker.CommandBufferLifetimes[priorHandle]
                        .SubmissionPinReceipt.Clear();
                }
                ClearSealedSubmissionContractScratch(commandCount);
                failureReason =
                    "sealed submission pin receipt could not be captured";
                fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                    .SealedSubmissionFallbackReason.PinCommit;
                return false;
            }

            tracker.LifetimeSubmissions.EnsureCapacity(
                checked(tracker.LifetimeSubmissions.Count + 1));
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                ulong handle = unchecked(
                    (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                VulkanCommandBufferLifetimeRecord lifetime =
                    tracker.CommandBufferLifetimes[handle];
                VulkanSubmissionPinReceipt pinReceipt =
                    lifetime.SubmissionPinReceipt;
                _ = tracker.TryResolveResourceSlotNoLock(
                    pinReceipt.CommandBufferSlot,
                    out VulkanResourceLifetimeRecord commandResource);
                commandResource.Pins.AddQueuedReference();
                commandResource.State |= EVulkanResourceLifetimeState.Queued;
                ReadOnlySpan<VulkanResourceSlotHandle> resourceSlots =
                    pinReceipt.Resources;
                for (int index = 0; index < resourceSlots.Length; ++index)
                {
                    _ = tracker.TryResolveResourceSlotNoLock(
                        resourceSlots[index],
                        out VulkanResourceLifetimeRecord resource);
                    resource.Pins.AddQueuedReference();
                    resource.State |= EVulkanResourceLifetimeState.Queued;
                }

                lifetime.QueuedSubmissionCount++;
                if (CommandBuffers.TrackingBatches.TryGetValue(
                        handle,
                        out VulkanCommandBufferTrackingBatch? trackingBatch))
                {
                    using (VulkanFrameLockScope.Enter(
                               trackingBatch,
                               EVulkanFrameWaitReason.ResourceLifetimeLock))
                        trackingBatch.QueuedSubmissionCount++;
                }
            }
        }

        ClearSealedSubmissionContractScratch(commandCount);
        return true;
    }

    private bool TryMatchSealedImageEntriesNoLock(
        SealedSubmissionContract contract,
        ref int overlayCount)
    {
        for (int imageIndex = 0; imageIndex < contract.Images.Length; ++imageIndex)
        {
            VulkanSealedImageDependency dependency = contract.Images[imageIndex];
            VulkanImageAccessState actual = default;
            bool foundOverlay = false;
            for (int overlayIndex = overlayCount - 1; overlayIndex >= 0; --overlayIndex)
            {
                VulkanSealedImageExitState overlay =
                    _sealedSubmissionImageOverlayScratch[overlayIndex];
                if (overlay.Key != dependency.Key)
                    continue;
                actual = overlay.State;
                foundOverlay = true;
                break;
            }

            if (!foundOverlay)
            {
                if (!Synchronization._trackedImageSubresourceStates.TryGetValue(
                        dependency.Key,
                        out VulkanImageSubresourceState? tracked) ||
                    tracked.PendingQueueOwnershipRelease is not null ||
                    tracked.SubmittedVersion != dependency.SubmittedStateVersion)
                {
                    return false;
                }
                actual = tracked.Submitted;
            }

            if (VulkanImageEntryStateContract.Compare(
                    actual,
                    dependency.RequiredEntryState) !=
                EVulkanPrimaryEntryStateMismatch.None)
            {
                return false;
            }
        }
        return true;
    }

    private bool TryAppendSealedImageExits(
        SealedSubmissionContract contract,
        ref int overlayCount)
    {
        for (int exitIndex = 0; exitIndex < contract.ImageExits.Length; ++exitIndex)
        {
            VulkanSealedImageExitState exit = contract.ImageExits[exitIndex];
            for (int overlayIndex = overlayCount - 1; overlayIndex >= 0; --overlayIndex)
            {
                if (_sealedSubmissionImageOverlayScratch[overlayIndex].Key != exit.Key)
                    continue;
                _sealedSubmissionImageOverlayScratch[overlayIndex] = exit;
                goto NextExit;
            }
            if (overlayCount >= _sealedSubmissionImageOverlayScratch.Length)
                return false;
            _sealedSubmissionImageOverlayScratch[overlayCount++] = exit;
        NextExit:;
        }
        return true;
    }

    private void ClearSealedSubmissionContractScratch(int count)
        => Array.Clear(_sealedSubmissionContractScratch, 0, count);

    /// <summary>
    /// Seals the ordinary graphics-primary contract at the recording boundary,
    /// moving graph scans and manifest construction out of the submit gateway.
    /// Command buffers submitted to another family safely miss the queue-family
    /// check and use the full path.
    /// </summary>
    private void TrySealRecordedGraphicsSubmissionContract(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        uint queueFamilyIndex = DeviceContext.QueueFamilies.GraphicsFamilyIndex ??
            Vk.QueueFamilyIgnored;
        if (handle == 0 || queueFamilyIndex == Vk.QueueFamilyIgnored)
            return;

        RecordSubmissionSealResult(
            TrySealSubmissionContract(handle, queueFamilyIndex, out var failureReason),
            failureReason);
    }

    private static void RecordSubmissionSealResult(
        bool sealedContract,
        RuntimeEngine.Rendering.Stats.Vulkan.SealedSubmissionSealFailureReason failureReason)
    {
        if (sealedContract)
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSealedSubmissionSeal();
        else
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSealedSubmissionSealFailure(
                failureReason);
    }

    private bool TrySealSubmissionContract(
        ulong handle,
        uint queueFamilyIndex,
        out RuntimeEngine.Rendering.Stats.Vulkan.SealedSubmissionSealFailureReason failureReason)
    {
        failureReason = RuntimeEngine.Rendering.Stats.Vulkan
            .SealedSubmissionSealFailureReason.ResourceState;
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        VulkanSealedResourceDependency[] resources;
        VulkanSealedDescriptorDependency[] descriptors;
        VulkanResourceSlotHandle commandSlot;
        ulong lifetimeRecordingGeneration;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (handle == 0 ||
                !tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) ||
                lifetime.Level != CommandBufferLevel.Primary ||
                !tracker.ResourceLifetimes.TryGetValue(
                    new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
                    out VulkanResourceLifetimeRecord? commandResource))
            {
                return false;
            }

            // Descriptor sets retain structural command state, but their
            // payload resources can change between recordings. Seed the seal
            // with the same exact payload closure used by the full gateway so
            // a first fast hit cannot omit queue pins for referenced objects.
            if (!RefreshSubmittedDescriptorDependencies_NoLock(
                    tracker,
                    lifetime,
                    out _,
                    out _))
            {
                failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                    .SealedSubmissionSealFailureReason.DescriptorPublication;
                return false;
            }

            resources = new VulkanSealedResourceDependency[
                lifetime.TouchedDependencies.Count];
            int descriptorCount = 0;
            for (int index = 0; index < resources.Length; ++index)
            {
                KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency =
                    lifetime.TouchedDependencies[index];
                if (!tracker.TryGetResourceSlotNoLock(
                        dependency.Key,
                        out VulkanResourceSlotHandle dependencySlot) ||
                    dependencySlot.Generation != dependency.Value)
                {
                    return false;
                }
                resources[index] = new VulkanSealedResourceDependency(
                    dependencySlot,
                    dependency.Key);
                if (dependency.Key.Type == ObjectType.DescriptorSet)
                    ++descriptorCount;
            }

            descriptors = new VulkanSealedDescriptorDependency[descriptorCount];
            int descriptorIndex = 0;
            for (int index = 0; index < resources.Length; ++index)
            {
                VulkanSealedResourceDependency dependency = resources[index];
                if (dependency.Key.Type != ObjectType.DescriptorSet)
                    continue;
                if (!tracker.TryGetPublishedDescriptorSnapshotNoLock(
                        dependency.Slot,
                        out VulkanPublishedDescriptorSetSnapshot snapshot) ||
                    !snapshot.IsNativePublicationKnown)
                {
                    failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionSealFailureReason.DescriptorPublication;
                    return false;
                }

                ReadOnlySpan<VulkanResourceSlotHandle> closure =
                    snapshot.ResourceClosure;
                for (int closureIndex = 0;
                     closureIndex < closure.Length;
                     ++closureIndex)
                {
                    if (ContainsSealedResourceSlot(
                            resources,
                            closure[closureIndex]))
                    {
                        continue;
                    }

                    failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionSealFailureReason.DescriptorPublication;
                    return false;
                }

                descriptors[descriptorIndex++] = new VulkanSealedDescriptorDependency(
                    dependency.Slot,
                    dependency.Key,
                    snapshot.ResourceClosureGeneration,
                    snapshot.ImagePayloadGeneration);
            }
            commandSlot = commandResource.Slot;
            lifetime.SubmissionPinReceipt.EnsureCapacity(resources.Length);
            lifetimeRecordingGeneration = lifetime.RecordingGeneration;
        }

        VulkanSealedImageDependency[] images;
        VulkanSealedImageExitState[] imageExits;
        ulong imageRecordingGeneration;
        using (VulkanFrameLockScope.Enter(
                   Synchronization._vulkanImageLayoutLock,
                   EVulkanFrameWaitReason.SynchronizationLock))
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    handle,
                    out VulkanRecordedImageLayoutState? recorded) ||
                recorded.EntryStateIncomplete)
            {
                failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                    .SealedSubmissionSealFailureReason.ImageState;
                return false;
            }
            if (recorded.QueueOwnershipTransfers.Count != 0)
            {
                failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                    .SealedSubmissionSealFailureReason.QueueOwnership;
                return false;
            }

            images = new VulkanSealedImageDependency[recorded.EntrySubresources.Count];
            int imageIndex = 0;
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> entry in
                     recorded.EntrySubresources)
            {
                if (!Synchronization._trackedImageSubresourceStates.TryGetValue(
                        entry.Key,
                        out VulkanImageSubresourceState? state))
                {
                    failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionSealFailureReason.ImageState;
                    return false;
                }
                images[imageIndex++] = new VulkanSealedImageDependency(
                    entry.Key,
                    entry.Value,
                    state.SubmittedVersion);
            }
            imageExits = new VulkanSealedImageExitState[
                recorded.TouchedSubresources.Count];
            for (int exitIndex = 0; exitIndex < imageExits.Length; ++exitIndex)
            {
                KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> exit =
                    recorded.TouchedSubresources[exitIndex];
                imageExits[exitIndex] = new VulkanSealedImageExitState(
                    exit.Key,
                    exit.Value);
            }
            imageRecordingGeneration = recorded.RecordingGeneration;
        }

        SealedSubmissionContract contract = new(
            handle,
            commandSlot,
            lifetimeRecordingGeneration,
            imageRecordingGeneration,
            queueFamilyIndex,
            resources,
            descriptors,
            images,
            imageExits);
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.RecordingGeneration == lifetimeRecordingGeneration)
            {
                lifetime.SealedSubmissionContract = contract;
                return true;
            }
        }
        failureReason = RuntimeEngine.Rendering.Stats.Vulkan
            .SealedSubmissionSealFailureReason.PublicationRace;
        return false;
    }

    private static bool ContainsSealedResourceSlot(
        ReadOnlySpan<VulkanSealedResourceDependency> resources,
        VulkanResourceSlotHandle slot)
    {
        for (int index = 0; index < resources.Length; ++index)
            if (resources[index].Slot == slot)
                return true;

        return false;
    }

    private unsafe bool TryValidateSealedSubmissionContractNoPins(
        Queue queue,
        ref SubmitInfo submitInfo,
        out bool isValid)
    {
        isValid = true;
        int commandCount = checked((int)submitInfo.CommandBufferCount);
        if (commandCount == 0 ||
            commandCount > _sealedSubmissionContractScratch.Length ||
            submitInfo.PCommandBuffers is null)
            return false;

        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        uint queueFamilyIndex = ResolveQueueFamilyIndex(queue);
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                ulong handle = unchecked(
                    (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (handle == 0 ||
                    !tracker.CommandBufferLifetimes.TryGetValue(
                        handle,
                        out VulkanCommandBufferLifetimeRecord? lifetime) ||
                    lifetime.SealedSubmissionContract is not { } candidate)
                {
                    ClearSealedSubmissionContractScratch(commandIndex);
                    isValid = false;
                    return false;
                }

                _sealedSubmissionContractScratch[commandIndex] = candidate;
                if (candidate.CommandBufferHandle != handle ||
                    candidate.QueueFamilyIndex != queueFamilyIndex ||
                    candidate.MatchResourceVectorNoLock(
                        tracker,
                        lifetime,
                        out _) !=
                        EVulkanSealedResourceMatch.Match)
                {
                    isValid = false;
                }
            }
        }

        using (VulkanFrameLockScope.Enter(
                   Synchronization._vulkanImageLayoutLock,
                   EVulkanFrameWaitReason.SynchronizationLock))
        {
            int overlayCount = 0;
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                ulong handle = unchecked(
                    (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                SealedSubmissionContract contract =
                    _sealedSubmissionContractScratch[commandIndex]!;
                if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                        handle,
                        out VulkanRecordedImageLayoutState? recorded) ||
                    recorded.RecordingGeneration != contract.ImageRecordingGeneration ||
                    recorded.EntryStateIncomplete ||
                    recorded.QueueOwnershipTransfers.Count != 0 ||
                    !TryMatchSealedImageEntriesNoLock(contract, ref overlayCount) ||
                    !TryAppendSealedImageExits(contract, ref overlayCount))
                {
                    isValid = false;
                }
            }
        }
        ClearSealedSubmissionContractScratch(commandCount);
        return true;
    }

    private unsafe void RefreshSealedSubmissionImageVersions(
        ref SubmitInfo submitInfo)
    {
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return;

        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        for (int commandIndex = 0;
             commandIndex < submitInfo.CommandBufferCount;
             ++commandIndex)
        {
            ulong handle = unchecked(
                (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
            SealedSubmissionContract? contract;
            using (VulkanFrameLockScope.Enter(
                       tracker.SyncRoot,
                       EVulkanFrameWaitReason.ResourceLifetimeLock))
            {
                contract = tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime)
                        ? lifetime.SealedSubmissionContract
                        : null;
            }
            if (contract is null)
                continue;

            using (VulkanFrameLockScope.Enter(
                       Synchronization._vulkanImageLayoutLock,
                       EVulkanFrameWaitReason.SynchronizationLock))
                contract.RefreshCurrentImageVersionsNoLock(Synchronization);
        }
    }
}
