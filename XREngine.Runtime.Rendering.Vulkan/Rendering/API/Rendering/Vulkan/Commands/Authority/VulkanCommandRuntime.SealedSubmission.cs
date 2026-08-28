using Silk.NET.Vulkan;

using System.Diagnostics;

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
        out RuntimeEngine.Rendering.Stats.Vulkan.SealedSubmissionFallbackReason fallbackReason,
        out long imageValidationTicks,
        out long queueOwnershipValidationTicks,
        out long lifetimePinAcquisitionTicks)
    {
        failureReason = string.Empty;
        imageValidationTicks = 0L;
        queueOwnershipValidationTicks = 0L;
        lifetimePinAcquisitionTicks = 0L;
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
        ulong completedGraphicsSequence;
        ulong completedTransferSequence;
        ulong completedOtherSequence;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            completedGraphicsSequence = tracker.CompletedGraphicsSequence;
            completedTransferSequence = tracker.CompletedTransferSequence;
            completedOtherSequence = tracker.CompletedOtherSequence;
        }

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
                if (!CommandBuffers.StableCommandDirectory.TryResolveByHandle(
                        handle,
                        out VulkanStableCommandSlotHandle identity,
                        out VulkanCommandBufferLifetimeRecord lifetime,
                        out VulkanCommandBufferTrackingBatch? trackingBatch) ||
                    lifetime.SealedSubmissionContract is not { } candidate ||
                    candidate.StableCommandIdentity != identity)
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

                if (trackingBatch is not null)
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
            // The full validator rebuilds this per submission. A sealed
            // acquire must do the same before it records any exact timeline
            // wait requirement, otherwise a prior submit can leak a stale
            // requirement into this one.
            Synchronization._submissionQueueSemaphoreRequirements.Clear();
            int overlayCount = 0;
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                SealedSubmissionContract contract =
                    _sealedSubmissionContractScratch[commandIndex]!;
                long imageStarted = Stopwatch.GetTimestamp();
                bool entriesMatch = TryMatchSealedImageEntriesNoLock(
                    contract,
                    ref overlayCount);
                imageValidationTicks += Stopwatch.GetTimestamp() - imageStarted;
                if (!entriesMatch)
                {
                    ClearSealedSubmissionContractScratch(commandCount);
                    failureReason = "sealed image vector changed";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.ImageVector;
                    return false;
                }

                long queueStarted = Stopwatch.GetTimestamp();
                bool queueOwnershipValid = ValidateQueueOwnershipTransferRequirements(
                        contract.QueueOwnershipTransfers,
                        queueFamilyIndex,
                        ref submitInfo,
                        commandIndex,
                        contract.CommandBufferHandle,
                        completedGraphicsSequence,
                        completedTransferSequence,
                        completedOtherSequence,
                        out _);
                queueOwnershipValidationTicks +=
                    Stopwatch.GetTimestamp() - queueStarted;
                if (!queueOwnershipValid)
                {
                    ClearSealedSubmissionContractScratch(commandCount);
                    failureReason = "sealed queue-ownership vector changed";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.ImageVector;
                    return false;
                }

                imageStarted = Stopwatch.GetTimestamp();
                bool exitsAppended = TryAppendSealedImageExits(
                    contract,
                    ref overlayCount);
                imageValidationTicks += Stopwatch.GetTimestamp() - imageStarted;
                if (!exitsAppended)
                {
                    ClearSealedSubmissionContractScratch(commandCount);
                    failureReason = "sealed image exit overlay capacity changed";
                    fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionFallbackReason.ImageVector;
                    return false;
                }
            }
        }

        // Revalidate and pin transactionally after image validation. The
        // submission-state gate prevents another submit from crossing this
        // boundary; the lifetime lock protects retirement and pin counts.
        long lifetimePinsStarted = Stopwatch.GetTimestamp();
        try
        {
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
                    if (!CommandBuffers.StableCommandDirectory.TryResolve(
                            contract.StableCommandIdentity,
                            handle,
                            contract.LifetimeRecordingGeneration,
                            out VulkanCommandBufferLifetimeRecord lifetime,
                            out _) ||
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
                    SealedSubmissionContract contract =
                        _sealedSubmissionContractScratch[commandIndex]!;
                    if (!CommandBuffers.StableCommandDirectory.TryResolve(
                            contract.StableCommandIdentity,
                            handle,
                            contract.LifetimeRecordingGeneration,
                            out VulkanCommandBufferLifetimeRecord lifetime,
                            out _))
                    {
                        ClearSealedSubmissionContractScratch(commandCount);
                        failureReason = "sealed command identity changed before pin capture";
                        fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                            .SealedSubmissionFallbackReason.PinCommit;
                        return false;
                    }
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
                        SealedSubmissionContract priorContract =
                            _sealedSubmissionContractScratch[priorIndex]!;
                        ulong priorHandle = unchecked((ulong)submitInfo.PCommandBuffers[priorIndex].Handle);
                        if (CommandBuffers.StableCommandDirectory.TryResolve(
                                priorContract.StableCommandIdentity,
                                priorHandle,
                                priorContract.LifetimeRecordingGeneration,
                                out VulkanCommandBufferLifetimeRecord priorLifetime,
                                out _))
                            priorLifetime.SubmissionPinReceipt.Clear();
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
                    SealedSubmissionContract contract =
                        _sealedSubmissionContractScratch[commandIndex]!;
                    if (!CommandBuffers.StableCommandDirectory.TryResolve(
                            contract.StableCommandIdentity,
                            handle,
                            contract.LifetimeRecordingGeneration,
                            out VulkanCommandBufferLifetimeRecord lifetime,
                            out VulkanCommandBufferTrackingBatch? trackingBatch))
                    {
                        ClearSealedSubmissionContractScratch(commandCount);
                        failureReason = "sealed command identity changed before publication";
                        fallbackReason = RuntimeEngine.Rendering.Stats.Vulkan
                            .SealedSubmissionFallbackReason.PinCommit;
                        return false;
                    }
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
                    if (trackingBatch is not null)
                    {
                        using (VulkanFrameLockScope.Enter(
                                   trackingBatch,
                                   EVulkanFrameWaitReason.ResourceLifetimeLock))
                            trackingBatch.QueuedSubmissionCount++;
                    }
                }
            }
        }
        finally
        {
            lifetimePinAcquisitionTicks +=
                Stopwatch.GetTimestamp() - lifetimePinsStarted;
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
                if (overlay.Slot != dependency.Slot)
                    continue;
                actual = overlay.State;
                foundOverlay = true;
                break;
            }

            if (!foundOverlay)
            {
                if (!Synchronization.TryGetStableImageSubresourceStateNoLock(
                        dependency.Slot,
                        out VulkanImageSubresourceState? tracked) ||
                    tracked!.PendingQueueOwnershipRelease is not null ||
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
                if (_sealedSubmissionImageOverlayScratch[overlayIndex].Slot != exit.Slot)
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
        CommandBuffer commandBuffer = default;
        commandBuffer.Handle = unchecked((nint)handle);
        PrimaryCommandArtifactOwner? owner =
            ResolvePreparedPrimaryOwner(commandBuffer);
        VulkanRecordedRenderTargetSnapshot renderTarget =
            owner?.RecordedDependencySignature.RenderTargetSnapshot ?? default;
        VulkanSealedResourceDependency[] renderTargetResources;
        VulkanSealedNestedCommandDependency[] nestedCommands;
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
            if (!TryCaptureSealedNestedCommands(
                    tracker,
                    owner,
                    out nestedCommands) ||
                !TryCaptureSealedRenderTargetResources(
                    tracker,
                    in renderTarget,
                    out renderTargetResources))
            {
                failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                    .SealedSubmissionSealFailureReason.ResourceState;
                return false;
            }
            lifetime.SubmissionPinReceipt.EnsureCapacity(resources.Length);
            lifetimeRecordingGeneration = lifetime.RecordingGeneration;
        }

        VulkanSealedImageDependency[] images;
        VulkanSealedImageExitState[] imageExits;
        VulkanQueueOwnershipTransferRequirement[] queueOwnershipTransfers;
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
            queueOwnershipTransfers = new VulkanQueueOwnershipTransferRequirement[
                recorded.QueueOwnershipTransfers.Count];
            for (int transferIndex = 0;
                 transferIndex < queueOwnershipTransfers.Length;
                 ++transferIndex)
            {
                VulkanQueueOwnershipTransferRequirement transfer =
                    recorded.QueueOwnershipTransfers[transferIndex];
                if (!transfer.IsValid ||
                    transfer.ResolveRole(queueFamilyIndex) ==
                        EVulkanQueueOwnershipTransferRole.Invalid)
                {
                    failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionSealFailureReason.QueueOwnership;
                    return false;
                }

                queueOwnershipTransfers[transferIndex] = transfer;
            }

            images = new VulkanSealedImageDependency[recorded.EntrySubresources.Count];
            int imageIndex = 0;
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> entry in
                     recorded.EntrySubresources)
            {
                if (!Synchronization._trackedImageSubresourceStates.TryGetValue(
                        entry.Key,
                        out VulkanImageSubresourceState? state) ||
                    !Synchronization.TryGetStableImageSubresourceStateNoLock(
                        state!.StableSlot,
                        out VulkanImageSubresourceState? stableState) ||
                    !ReferenceEquals(state, stableState) ||
                    state.PendingQueueOwnershipRelease is not null ||
                    !IsSealedImageStateQueueCompatible(entry.Value, queueFamilyIndex) ||
                    !IsSealedImageStateQueueCompatible(state.Submitted, queueFamilyIndex) ||
                    VulkanImageEntryStateContract.Compare(state.Submitted, entry.Value) !=
                        EVulkanPrimaryEntryStateMismatch.None)
                {
                    failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionSealFailureReason.ImageState;
                    return false;
                }
                images[imageIndex++] = new VulkanSealedImageDependency(
                    state.StableSlot,
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
                    ResolveSealedImageExitSlotNoLock(exit.Key, exit.Value, queueFamilyIndex),
                    exit.Value);
                if (!imageExits[exitIndex].Slot.IsValid)
                {
                    failureReason = RuntimeEngine.Rendering.Stats.Vulkan
                        .SealedSubmissionSealFailureReason.ImageState;
                    return false;
                }
            }
            imageRecordingGeneration = recorded.RecordingGeneration;
        }

        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.RecordingGeneration == lifetimeRecordingGeneration &&
                CommandBuffers.StableCommandDirectory.TryPublish(
                    handle,
                    lifetimeRecordingGeneration,
                    lifetime,
                    CommandBuffers.TrackingBatches.TryGetValue(
                        handle,
                        out VulkanCommandBufferTrackingBatch? trackingBatch)
                        ? trackingBatch
                        : null,
                    out VulkanStableCommandSlotHandle stableCommandIdentity))
            {
                SealedSubmissionContract contract = new(
                    handle,
                    stableCommandIdentity,
                    commandSlot,
                    lifetimeRecordingGeneration,
                    imageRecordingGeneration,
                    queueFamilyIndex,
                    resources,
                    descriptors,
                    images,
                    imageExits,
                    queueOwnershipTransfers,
                    renderTarget,
                    renderTargetResources,
                    nestedCommands);
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

    private static bool TryCaptureSealedNestedCommands(
        VulkanResourceLifetimeTracker tracker,
        PrimaryCommandArtifactOwner? owner,
        out VulkanSealedNestedCommandDependency[] dependencies)
    {
        if (owner is null || owner.RecordedSecondaryArtifactSequence.Count == 0)
        {
            dependencies = [];
            return true;
        }

        dependencies = new
            VulkanSealedNestedCommandDependency[
                owner.RecordedSecondaryArtifactSequence.Count];
        for (int index = 0; index < dependencies.Length; ++index)
        {
            VulkanRecordedCommandArtifactReference artifact = owner
                .RecordedSecondaryArtifactSequence.GetEntry(index).Artifact;
            ulong handle = unchecked((ulong)artifact.NativeBuffer.Handle);
            VulkanResourceLifetimeKey key = new(ObjectType.CommandBuffer, handle);
            if (!artifact.IsExecutable || handle == 0UL ||
                !tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) ||
                lifetime.Level != artifact.Level ||
                lifetime.RecordingGeneration != artifact.RecordingGeneration ||
                !tracker.TryGetResourceSlotNoLock(
                    key,
                    out VulkanResourceSlotHandle slot) ||
                !tracker.TryResolvePublishedResourceSlotNoLock(slot, out _))
            {
                dependencies = [];
                return false;
            }

            dependencies[index] = new(artifact, slot, lifetime);
        }
        return true;
    }

    private static bool TryCaptureSealedRenderTargetResources(
        VulkanResourceLifetimeTracker tracker,
        in VulkanRecordedRenderTargetSnapshot renderTarget,
        out VulkanSealedResourceDependency[] dependencies)
    {
        if (!renderTarget.IsComplete)
        {
            dependencies = [];
            return true;
        }

        int framebufferCount = renderTarget.FramebufferHandle == 0UL ? 0 : 1;
        dependencies = new VulkanSealedResourceDependency[
            framebufferCount + renderTarget.AttachmentCount * 2];
        int destination = 0;
        if (framebufferCount != 0 &&
            !TryCaptureSealedResource(
                tracker,
                ObjectType.Framebuffer,
                renderTarget.FramebufferHandle,
                renderTarget.FramebufferGeneration,
                out dependencies[destination++]))
        {
            dependencies = [];
            return false;
        }

        for (int index = 0; index < renderTarget.AttachmentCount; ++index)
        {
            VulkanNativeAttachmentIdentity attachment = renderTarget.GetAttachment(index);
            if (!attachment.IsComplete ||
                !TryCaptureSealedResource(
                    tracker,
                    ObjectType.Image,
                    attachment.ImageHandle,
                    attachment.ImageGeneration,
                    out dependencies[destination++]) ||
                !TryCaptureSealedResource(
                    tracker,
                    ObjectType.ImageView,
                    attachment.ImageViewHandle,
                    attachment.ImageViewGeneration,
                    out dependencies[destination++]))
            {
                dependencies = [];
                return false;
            }
        }

        return true;
    }

    private static bool TryCaptureSealedResource(
        VulkanResourceLifetimeTracker tracker,
        ObjectType type,
        ulong handle,
        ulong generation,
        out VulkanSealedResourceDependency dependency)
    {
        VulkanResourceLifetimeKey key = new(type, handle);
        if (generation == 0UL ||
            !tracker.TryGetResourceSlotNoLock(key, out VulkanResourceSlotHandle slot) ||
            !tracker.TryResolvePublishedResourceSlotNoLock(
                slot,
                out VulkanResourceLifetimeRecord resource) ||
            resource.Generation != generation)
        {
            dependency = default;
            return false;
        }

        dependency = new VulkanSealedResourceDependency(slot, key, generation);
        return true;
    }

    private VulkanStableImageSubresourceSlotHandle ResolveSealedImageExitSlotNoLock(
        VulkanTrackedImageSubresource key,
        VulkanImageAccessState state,
        uint queueFamilyIndex)
    {
        if (!IsSealedImageStateQueueCompatible(state, queueFamilyIndex) ||
            !Synchronization._trackedImageSubresourceStates.TryGetValue(
                key,
                out VulkanImageSubresourceState? tracked) ||
            !Synchronization.TryGetStableImageSubresourceStateNoLock(
                tracked.StableSlot,
                out VulkanImageSubresourceState? stableState) ||
            !ReferenceEquals(tracked, stableState) ||
            (state.ResourceGeneration != 0UL &&
             tracked.Submitted.ResourceGeneration != state.ResourceGeneration))
        {
            return VulkanStableImageSubresourceSlotHandle.Invalid;
        }

        return tracked.StableSlot;
    }

    private static bool IsSealedImageStateQueueCompatible(
        in VulkanImageAccessState state,
        uint queueFamilyIndex)
        => state.QueueFamilyIndex == Vk.QueueFamilyIgnored ||
           state.QueueFamilyIndex == queueFamilyIndex;

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
        ulong completedGraphicsSequence;
        ulong completedTransferSequence;
        ulong completedOtherSequence;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            completedGraphicsSequence = tracker.CompletedGraphicsSequence;
            completedTransferSequence = tracker.CompletedTransferSequence;
            completedOtherSequence = tracker.CompletedOtherSequence;
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                ulong handle = unchecked(
                    (ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (handle == 0 ||
                    !CommandBuffers.StableCommandDirectory.TryResolveByHandle(
                        handle,
                        out VulkanStableCommandSlotHandle identity,
                        out VulkanCommandBufferLifetimeRecord lifetime,
                        out _) ||
                    lifetime.SealedSubmissionContract is not { } candidate ||
                    candidate.StableCommandIdentity != identity)
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
            Synchronization._submissionQueueSemaphoreRequirements.Clear();
            int overlayCount = 0;
            for (int commandIndex = 0; commandIndex < commandCount; ++commandIndex)
            {
                SealedSubmissionContract contract =
                    _sealedSubmissionContractScratch[commandIndex]!;
                if (!TryMatchSealedImageEntriesNoLock(contract, ref overlayCount) ||
                    !ValidateQueueOwnershipTransferRequirements(
                        contract.QueueOwnershipTransfers,
                        queueFamilyIndex,
                        ref submitInfo,
                        commandIndex,
                        contract.CommandBufferHandle,
                        completedGraphicsSequence,
                        completedTransferSequence,
                        completedOtherSequence,
                        out _) ||
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
            SealedSubmissionContract? contract = null;
            using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
            {
                if (CommandBuffers.StableCommandDirectory.TryResolveByHandle(
                        handle,
                        out VulkanStableCommandSlotHandle identity,
                        out VulkanCommandBufferLifetimeRecord lifetime,
                        out _) &&
                    lifetime.SealedSubmissionContract is { } candidate &&
                    candidate.StableCommandIdentity == identity)
                    contract = candidate;
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
