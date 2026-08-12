using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>Publishes a completed fence to both lifetime and image ledgers.</summary>
    internal void CompleteTrackedFence(Fence fence)
    {
        ResourceRuntime.CompleteSynchronousFence(fence);
        Synchronization.AdvanceCompletedImageLayouts(ResourceRuntime.Lifetime.Tracker);
    }

    internal void CompleteTrackedTimeline(Semaphore semaphore, ulong value)
    {
        ResourceRuntime.CompleteTimeline(semaphore, value);
        Synchronization.AdvanceCompletedImageLayouts(ResourceRuntime.Lifetime.Tracker);
    }

    internal void CompleteTrackedQueue(Queue queue)
    {
        ResourceRuntime.CompleteQueue(queue);
        Synchronization.AdvanceCompletedImageLayouts(ResourceRuntime.Lifetime.Tracker);
    }

    internal void CompleteTrackedDevice()
    {
        ResourceRuntime.CompleteDevice();
        Synchronization.AdvanceCompletedImageLayouts(ResourceRuntime.Lifetime.Tracker);
    }

    internal void MarkTrackedDeviceLost()
        => ResourceRuntime.MarkDeviceLost();

    /// <summary>
    /// Owns the complete validation-to-native-dispatch transaction for tracked
    /// command buffers. A successful native dispatch remains accepted even if
    /// later diagnostic or publication work degrades.
    /// </summary>
    internal VulkanSubmissionReceipt SubmitToQueueTrackedWithDisposition(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        out bool queueDispatchAttempted,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage,
        [CallerMemberName] string? caller = null)
    {
        queueDispatchAttempted = false;
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        if (!DeviceContext.IsOperational)
        {
            ResolveSubmissionMarkers(ref submitInfo, false);
            Synchronization.RecordQueueOperation(
                DeviceContext.State,
                "submit-rejected",
                queue,
                Result.ErrorDeviceLost,
                diagnosticContext.SubmissionSerial,
                caller);
            return VulkanSubmissionReceipt.Rejected(Result.ErrorDeviceLost);
        }

        using VulkanQueueOperationLease queueOperation = VulkanQueueOperationLease.TryEnter(
            CommandBuffers.OneTimeSubmitGate,
            DeviceContext.StateMachine,
            FrameTelemetry);
        if (!queueOperation.Acquired)
        {
            ResolveSubmissionMarkers(ref submitInfo, false);
            Synchronization.RecordQueueOperation(
                DeviceContext.State,
                "submit-rejected",
                queue,
                Result.ErrorDeviceLost,
                diagnosticContext.SubmissionSerial,
                caller);
            return VulkanSubmissionReceipt.Rejected(Result.ErrorDeviceLost);
        }

        DeviceContext.RecordSubmissionDiagnostics(diagnosticContext);
        if (!ValidateOrderedCommandBufferImageStateContracts(queue, ref submitInfo, out _) ||
            !TryAcquireSubmissionLifetimePins(
                ref submitInfo,
                in diagnosticContext,
                out _,
                out injectedFailureStage))
        {
            ResolveSubmissionMarkers(ref submitInfo, false);
            Synchronization.RecordQueueOperation(
                DeviceContext.State,
                "submit-rejected-validation",
                queue,
                Result.ErrorValidationFailedExt,
                diagnosticContext.SubmissionSerial,
                caller);
            return VulkanSubmissionReceipt.Rejected(Result.ErrorValidationFailedExt);
        }

        bool submissionAccepted = false;
        bool lifetimePinsTransferred = false;
        bool publicationSucceeded = true;
        Result result = Result.ErrorUnknown;
        try
        {
            if (diagnosticContext.OpenXrStrictSpsFaultInjectionStage ==
                EOpenXrStrictSpsFaultInjectionStage.Submit)
            {
                injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.Submit;
                ResolveSubmissionMarkers(ref submitInfo, false);
                return VulkanSubmissionReceipt.Rejected(Result.ErrorValidationFailedExt);
            }

            queueDispatchAttempted = true;
            using (VulkanCpuStageScope stage = new(FrameTelemetry, EVulkanCpuStage.QueueSubmit))
            {
                result = SubmitNative(queue, ref submitInfo, fence);
                submissionAccepted = result == Result.Success;
            }
            DeviceContext.ObserveNativeResult($"vkQueueSubmit:{caller ?? "<unknown>"}", result);
            Synchronization.RecordQueueOperation(
                DeviceContext.State,
                "submit",
                queue,
                result,
                diagnosticContext.SubmissionSerial,
                caller);

            if (!submissionAccepted)
            {
                ResolveSubmissionMarkers(ref submitInfo, false);
                return VulkanSubmissionReceipt.Rejected(result);
            }

            ResolveSubmissionMarkers(ref submitInfo, true);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueSubmit();
            try
            {
                VulkanLifetimeSubmission submission = PublishSuccessfulSubmissionLifetime(
                    queue,
                    ref submitInfo,
                    fence,
                    in diagnosticContext);
                lifetimePinsTransferred = true;
                PublishRecordedImageLayouts(queue, ref submitInfo, in submission);
            }
            catch
            {
                publicationSucceeded = false;
            }
        }
        catch when (submissionAccepted)
        {
            publicationSucceeded = false;
        }
        finally
        {
            if (!submissionAccepted || lifetimePinsTransferred)
            {
                try
                {
                    ReleaseSubmissionLifetimePins(ref submitInfo);
                }
                catch
                {
                    publicationSucceeded = false;
                }
            }
        }

        return new VulkanSubmissionReceipt(
            result,
            submissionAccepted,
            lifetimePinsTransferred,
            publicationSucceeded);
    }

    private unsafe bool ValidateOrderedCommandBufferImageStateContracts(
        Queue queue,
        ref SubmitInfo submitInfo,
        out string failureReason)
    {
        failureReason = string.Empty;
        Synchronization._submissionQueueSemaphoreRequirements.Clear();
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return true;

        uint submissionQueueFamilyIndex = ResolveQueueFamilyIndex(queue);
        ulong completedGraphicsSequence;
        ulong completedTransferSequence;
        ulong completedOtherSequence;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            completedGraphicsSequence = ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence;
            completedTransferSequence = ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence;
            completedOtherSequence = ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence;
        }

        lock (Synchronization._vulkanImageLayoutLock)
        {
            Synchronization._submissionImageStateScratch.Clear();
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (handle == 0 ||
                    !Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                        handle,
                        out VulkanRecordedImageLayoutState? recorded))
                {
                    continue;
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> entry in
                         recorded.EntrySubresources)
                {
                    VulkanImageAccessState actual;
                    if (!Synchronization._submissionImageStateScratch.TryGetValue(entry.Key, out actual))
                    {
                        if (!Synchronization._trackedImageSubresourceStates.TryGetValue(
                                entry.Key,
                                out VulkanImageSubresourceState? tracked))
                        {
                            failureReason =
                                $"command buffer 0x{handle:X} requires missing submitted image state for 0x{entry.Key.ImageHandle:X}.";
                            return false;
                        }
                        actual = tracked.Submitted;
                    }

                    if (Synchronization._trackedImageSubresourceStates.TryGetValue(
                            entry.Key,
                            out VulkanImageSubresourceState? trackedState) &&
                        trackedState.PendingQueueOwnershipRelease is
                            VulkanPendingQueueOwnershipRelease pendingRelease &&
                        !HasPairedQueueOwnershipAcquire(
                            recorded,
                            entry.Key,
                            submissionQueueFamilyIndex,
                            in pendingRelease))
                    {
                        failureReason =
                            $"command buffer 0x{handle:X} accesses image 0x{entry.Key.ImageHandle:X} while queue ownership release " +
                            $"{pendingRelease.Requirement.SourceQueueFamilyIndex}->{pendingRelease.Requirement.DestinationQueueFamilyIndex} is pending without a paired acquire";
                        return false;
                    }

                    EVulkanPrimaryEntryStateMismatch mismatch =
                        VulkanImageEntryStateContract.Compare(actual, entry.Value);
                    if (mismatch != EVulkanPrimaryEntryStateMismatch.None)
                    {
                        failureReason =
                            $"command buffer 0x{handle:X} image 0x{entry.Key.ImageHandle:X} entry state mismatch: {mismatch}.";
                        return false;
                    }
                }

                if (!ValidateQueueOwnershipTransferRequirements(
                        recorded,
                        submissionQueueFamilyIndex,
                        ref submitInfo,
                        commandIndex,
                        handle,
                        completedGraphicsSequence,
                        completedTransferSequence,
                        completedOtherSequence,
                        out failureReason))
                {
                    return false;
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> touched in
                         recorded.TouchedSubresources)
                {
                    Synchronization._submissionImageStateScratch[touched.Key] = touched.Value;
                }
            }
        }
        return true;
    }

    private bool ValidateQueueOwnershipTransferRequirements(
        VulkanRecordedImageLayoutState recorded,
        uint submissionQueueFamilyIndex,
        ref SubmitInfo submitInfo,
        int commandIndex,
        ulong commandBufferHandle,
        ulong completedGraphicsSequence,
        ulong completedTransferSequence,
        ulong completedOtherSequence,
        out string failureReason)
    {
        failureReason = string.Empty;
        for (int transferIndex = 0; transferIndex < recorded.QueueOwnershipTransfers.Count; transferIndex++)
        {
            VulkanQueueOwnershipTransferRequirement requirement = recorded.QueueOwnershipTransfers[transferIndex];
            EVulkanQueueOwnershipTransferRole role = requirement.ResolveRole(submissionQueueFamilyIndex);
            if (role == EVulkanQueueOwnershipTransferRole.Invalid)
            {
                failureReason =
                    $"commandBuffer[{commandIndex}]=0x{commandBufferHandle:X} records queue ownership " +
                    $"{requirement.SourceQueueFamilyIndex}->{requirement.DestinationQueueFamilyIndex}, but submits to queue family {submissionQueueFamilyIndex}";
                return false;
            }

            uint levelCount = Math.Max(requirement.Range.LevelCount, 1u);
            uint layerCount = Math.Max(requirement.Range.LayerCount, 1u);
            for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                    if (!ValidateQueueOwnershipTransferAspect(
                            in requirement,
                            role,
                            requirement.Range.BaseMipLevel + mipOffset,
                            requirement.Range.BaseArrayLayer + layerOffset,
                            ImageAspectFlags.ColorBit,
                            ref submitInfo,
                            completedGraphicsSequence,
                            completedTransferSequence,
                            completedOtherSequence,
                            out failureReason) ||
                        !ValidateQueueOwnershipTransferAspect(
                            in requirement,
                            role,
                            requirement.Range.BaseMipLevel + mipOffset,
                            requirement.Range.BaseArrayLayer + layerOffset,
                            ImageAspectFlags.DepthBit,
                            ref submitInfo,
                            completedGraphicsSequence,
                            completedTransferSequence,
                            completedOtherSequence,
                            out failureReason) ||
                        !ValidateQueueOwnershipTransferAspect(
                            in requirement,
                            role,
                            requirement.Range.BaseMipLevel + mipOffset,
                            requirement.Range.BaseArrayLayer + layerOffset,
                            ImageAspectFlags.StencilBit,
                            ref submitInfo,
                            completedGraphicsSequence,
                            completedTransferSequence,
                            completedOtherSequence,
                            out failureReason))
                    {
                        return false;
                    }
        }
        return true;
    }

    private bool ValidateQueueOwnershipTransferAspect(
        in VulkanQueueOwnershipTransferRequirement requirement,
        EVulkanQueueOwnershipTransferRole role,
        uint mipLevel,
        uint arrayLayer,
        ImageAspectFlags aspect,
        ref SubmitInfo submitInfo,
        ulong completedGraphicsSequence,
        ulong completedTransferSequence,
        ulong completedOtherSequence,
        out string failureReason)
    {
        failureReason = string.Empty;
        if ((requirement.Range.AspectMask & aspect) == 0)
            return true;

        VulkanTrackedImageSubresource key = new(requirement.ImageHandle, mipLevel, arrayLayer, aspect);
        Synchronization._trackedImageSubresourceStates.TryGetValue(key, out VulkanImageSubresourceState? trackedState);
        if (role == EVulkanQueueOwnershipTransferRole.Release)
        {
            if (trackedState?.PendingQueueOwnershipRelease is not null)
            {
                failureReason = $"image 0x{key.ImageHandle:X} already has a pending queue-ownership release";
                return false;
            }
            if (trackedState is not null &&
                trackedState.Submitted.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                trackedState.Submitted.QueueFamilyIndex != requirement.SourceQueueFamilyIndex)
            {
                failureReason =
                    $"queue-ownership release for image 0x{key.ImageHandle:X} expects source family {requirement.SourceQueueFamilyIndex}, " +
                    $"but submitted ownership is {trackedState.Submitted.QueueFamilyIndex}";
                return false;
            }
            return true;
        }

        if (trackedState?.PendingQueueOwnershipRelease is not VulkanPendingQueueOwnershipRelease pendingRelease)
        {
            failureReason = $"queue-ownership acquire for image 0x{key.ImageHandle:X} has no submitted release";
            return false;
        }
        if (!pendingRelease.Requirement.IsPairedWith(
                in requirement,
                key.ImageHandle,
                key.MipLevel,
                key.ArrayLayer,
                key.Aspect))
        {
            failureReason = $"queue-ownership acquire for image 0x{key.ImageHandle:X} does not match its submitted release";
            return false;
        }

        VulkanLifetimeSubmission releaseSubmission = pendingRelease.Submission;
        if (IsSubmissionCompleted(
                in releaseSubmission,
                completedGraphicsSequence,
                completedTransferSequence,
                completedOtherSequence))
            return true;

        VulkanQueueSemaphoreRequirement semaphoreRequirement = new(
            releaseSubmission.TimelineSemaphoreHandle,
            releaseSubmission.TimelineValue,
            requirement.DestinationStageMask,
            requirement.SourceQueueFamilyIndex,
            requirement.DestinationQueueFamilyIndex);
        if (!semaphoreRequirement.IsValid)
        {
            failureReason = $"queue-ownership acquire for image 0x{key.ImageHandle:X} depends on an incomplete submission without a timeline semaphore";
            return false;
        }
        if (!Synchronization._submissionQueueSemaphoreRequirements.Contains(semaphoreRequirement))
            Synchronization._submissionQueueSemaphoreRequirements.Add(semaphoreRequirement);
        if (SubmissionSatisfiesQueueSemaphoreRequirement(ref submitInfo, in semaphoreRequirement))
            return true;

        failureReason =
            $"queue-ownership acquire for image 0x{key.ImageHandle:X} requires timeline semaphore " +
            $"0x{semaphoreRequirement.SemaphoreHandle:X} value {semaphoreRequirement.Value}";
        return false;
    }

    private static bool IsSubmissionCompleted(
        in VulkanLifetimeSubmission submission,
        ulong completedGraphicsSequence,
        ulong completedTransferSequence,
        ulong completedOtherSequence)
        => submission.QueueSequence != 0 && submission.QueueDomain switch
        {
            EVulkanLifetimeQueueDomain.Graphics => submission.QueueSequence <= completedGraphicsSequence,
            EVulkanLifetimeQueueDomain.Transfer => submission.QueueSequence <= completedTransferSequence,
            _ => submission.QueueSequence <= completedOtherSequence,
        };

    private static bool HasPairedQueueOwnershipAcquire(
        VulkanRecordedImageLayoutState recorded,
        VulkanTrackedImageSubresource key,
        uint submissionQueueFamilyIndex,
        in VulkanPendingQueueOwnershipRelease pendingRelease)
    {
        for (int transferIndex = 0; transferIndex < recorded.QueueOwnershipTransfers.Count; transferIndex++)
        {
            VulkanQueueOwnershipTransferRequirement requirement = recorded.QueueOwnershipTransfers[transferIndex];
            if (requirement.ResolveRole(submissionQueueFamilyIndex) == EVulkanQueueOwnershipTransferRole.Acquire &&
                pendingRelease.Requirement.IsPairedWith(
                    in requirement,
                    key.ImageHandle,
                    key.MipLevel,
                    key.ArrayLayer,
                    key.Aspect))
                return true;
        }
        return false;
    }

    private static unsafe bool SubmissionSatisfiesQueueSemaphoreRequirement(
        ref SubmitInfo submitInfo,
        in VulkanQueueSemaphoreRequirement requirement)
    {
        TimelineSemaphoreSubmitInfo* timeline = FindTrackedTimelineInfo(submitInfo.PNext);
        if (timeline is null ||
            timeline->PWaitSemaphoreValues is null ||
            submitInfo.PWaitSemaphores is null ||
            submitInfo.PWaitDstStageMask is null)
            return false;

        uint waitCount = Math.Min(timeline->WaitSemaphoreValueCount, submitInfo.WaitSemaphoreCount);
        ReadOnlySpan<Semaphore> waitSemaphores = new(
            submitInfo.PWaitSemaphores,
            checked((int)waitCount));
        ReadOnlySpan<ulong> waitValues = new(
            timeline->PWaitSemaphoreValues,
            checked((int)waitCount));
        ReadOnlySpan<PipelineStageFlags> waitStages = new(
            submitInfo.PWaitDstStageMask,
            checked((int)waitCount));
        for (uint waitIndex = 0; waitIndex < waitCount; waitIndex++)
            if (requirement.IsSatisfiedBy(
                    waitSemaphores[(int)waitIndex].Handle,
                    waitValues[(int)waitIndex],
                    (PipelineStageFlags2)(ulong)(waitStages[(int)waitIndex] == 0
                        ? PipelineStageFlags.AllCommandsBit
                        : waitStages[(int)waitIndex])))
                return true;
        return false;
    }

    private uint ResolveQueueFamilyIndex(Queue queue)
    {
        QueueFamilyIndices families = DeviceContext.QueueFamilies;
        if (queue.Handle == DeviceContext.GraphicsQueue.Handle ||
            queue.Handle == DeviceContext.SecondaryGraphicsQueue.Handle)
            return families.GraphicsFamilyIndex ?? Vk.QueueFamilyIgnored;
        if (queue.Handle == DeviceContext.ComputeQueue.Handle)
            return families.ComputeFamilyIndex ?? families.GraphicsFamilyIndex ?? Vk.QueueFamilyIgnored;
        if (queue.Handle == DeviceContext.TransferQueue.Handle)
            return families.TransferFamilyIndex ?? families.GraphicsFamilyIndex ?? Vk.QueueFamilyIgnored;
        if (queue.Handle == DeviceContext.PresentQueue.Handle)
            return families.PresentFamilyIndex ?? families.GraphicsFamilyIndex ?? Vk.QueueFamilyIgnored;
        return Vk.QueueFamilyIgnored;
    }

    private unsafe bool TryAcquireSubmissionLifetimePins(
        ref SubmitInfo submitInfo,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        out string failureReason,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage)
    {
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                CommandBuffer commandBuffer = submitInfo.PCommandBuffers[commandIndex];
                ulong handle = unchecked((ulong)commandBuffer.Handle);
                if (handle == 0)
                    continue;

                for (int previousIndex = 0; previousIndex < commandIndex; previousIndex++)
                    if (submitInfo.PCommandBuffers[previousIndex].Handle == commandBuffer.Handle)
                    {
                        failureReason = $"submission contains command buffer 0x{handle:X} more than once";
                        return false;
                    }

                if (tracker.CommandBufferLifetimes.TryGetValue(
                        handle,
                        out VulkanCommandBufferLifetimeRecord? queuedLifetime) &&
                    queuedLifetime.QueuedSubmissionCount != 0)
                {
                    failureReason = $"command buffer 0x{handle:X} already occupies the submission gateway";
                    return false;
                }

                if (CommandBuffers.TrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
                    lock (batch)
                        if (batch.QueuedSubmissionCount != 0)
                        {
                            failureReason = $"command buffer 0x{handle:X} tracking batch is already queued";
                            return false;
                        }
            }

            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                CommandBuffer commandBuffer = submitInfo.PCommandBuffers[commandIndex];
                ulong handle = unchecked((ulong)commandBuffer.Handle);
                if (handle == 0)
                    continue;
                VulkanCommandBufferLifetimeRecord lifetime = tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? existing)
                        ? existing
                        : tracker.CommandBufferLifetimes[handle] = new VulkanCommandBufferLifetimeRecord();
                lifetime.QueuedSubmissionCount++;
                if (CommandBuffers.TrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
                    lock (batch)
                    {
                        batch.IsRecording = false;
                        batch.QueuedSubmissionCount++;
                    }
            }
        }

        bool retainGatewayPins = false;
        try
        {
            if (diagnosticContext.OpenXrStrictSpsFaultInjectionStage ==
                EOpenXrStrictSpsFaultInjectionStage.LifetimeValidation)
            {
                injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.LifetimeValidation;
                failureReason = "injected strict-SPS lifetime-validation boundary failure";
                return false;
            }

            for (int index = 0; index < submitInfo.CommandBufferCount; index++)
                if (!TryFlushCommandBufferTrackingBatch(submitInfo.PCommandBuffers[index], out failureReason))
                    return false;

            lock (tracker.SyncRoot)
            {
                for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
                {
                    ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                    if (handle == 0)
                        continue;
                    VulkanResourceLifetimeKey commandKey = new(ObjectType.CommandBuffer, handle);
                    VulkanResourceLifetimeRecord commandResource = tracker.GetOrRegisterResourceNoLock(
                        commandKey,
                        "CommandBuffer.SubmitGateway");
                    if ((commandResource.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                                  EVulkanResourceLifetimeState.Destroyed)) != 0)
                    {
                        failureReason = $"command buffer 0x{handle:X} is retired";
                        return false;
                    }

                    VulkanCommandBufferLifetimeRecord lifetime = tracker.CommandBufferLifetimes[handle];
                    if (!RefreshSubmittedDescriptorDependencies_NoLock(
                            tracker,
                            lifetime,
                            out VulkanResourceLifetimeKey descriptorFailureKey,
                            out string descriptorFailureReason))
                    {
                        failureReason =
                            $"command buffer 0x{handle:X} descriptor dependency {descriptorFailureKey} is invalid: {descriptorFailureReason}";
                        return false;
                    }
                    foreach ((VulkanResourceLifetimeKey key, ulong generation) in lifetime.TouchedDependencies)
                    {
                        if (!tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                            resource.Generation != generation ||
                            (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                        {
                            failureReason = $"recorded dependency {key} generation {generation} is unavailable";
                            return false;
                        }
                    }
                }

                for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
                {
                    ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                    if (handle == 0)
                        continue;
                    VulkanResourceLifetimeRecord commandResource = tracker.GetOrRegisterResourceNoLock(
                        new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
                        "CommandBuffer.SubmitQueuePin");
                    commandResource.Pins.AddQueuedReference();
                    commandResource.State |= EVulkanResourceLifetimeState.Queued;
                    foreach ((VulkanResourceLifetimeKey key, _) in tracker.CommandBufferLifetimes[handle].TouchedDependencies)
                    {
                        VulkanResourceLifetimeRecord resource = tracker.ResourceLifetimes[key];
                        resource.Pins.AddQueuedReference();
                        resource.State |= EVulkanResourceLifetimeState.Queued;
                    }
                }
            }

            retainGatewayPins = true;
            failureReason = string.Empty;
            return true;
        }
        finally
        {
            if (!retainGatewayPins)
                ReleaseSubmissionGatewayPins(ref submitInfo);
        }
    }

    internal bool ValidateSubmissionResourceLifetimes(
        ref SubmitInfo submitInfo,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        out string failureReason,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage)
        => TryAcquireSubmissionLifetimePins(
            ref submitInfo,
            in diagnosticContext,
            out failureReason,
            out injectedFailureStage);

    private static bool RefreshSubmittedDescriptorDependencies_NoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanCommandBufferLifetimeRecord commandLifetime,
        out VulkanResourceLifetimeKey failureKey,
        out string failureReason)
    {
        // Descriptor contents are mutable per completed frame slot. Only the set
        // handle is structural command state, so refresh the concrete referenced
        // generations at the submission gateway before validating and pinning.
        commandLifetime.RefreshTouchedDependencies();
        List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> touched = commandLifetime.TouchedDependencies;
        int descriptorScanCount = touched.Count;
        Dictionary<VulkanResourceLifetimeKey, ulong> touchedGenerations =
            tracker.SubmissionDependencyGenerationsScratch;
        touchedGenerations.Clear();
        for (int index = 0; index < descriptorScanCount; index++)
            touchedGenerations[touched[index].Key] = touched[index].Value;

        for (int index = 0; index < descriptorScanCount; index++)
        {
            VulkanResourceLifetimeKey descriptorSetKey = touched[index].Key;
            if (descriptorSetKey.Type != ObjectType.DescriptorSet ||
                !tracker.PublishedDescriptorSets.TryGetValue(
                    descriptorSetKey.Handle,
                    out VulkanPublishedDescriptorSetSnapshot? snapshot))
                continue;

            for (int referenceIndex = 0; referenceIndex < snapshot.References.Length; referenceIndex++)
            {
                VulkanResourceLifetimeKey referenceKey = snapshot.References[referenceIndex];
                if (TryAppendSubmittedDescriptorDependency_NoLock(
                        tracker,
                        touched,
                        touchedGenerations,
                        referenceKey,
                        out failureReason))
                    continue;

                failureKey = referenceKey;
                failureReason =
                    $"{failureReason}; referenced by {descriptorSetKey} snapshotGeneration={snapshot.Generation}";
                return false;
            }
        }

        failureKey = default;
        failureReason = string.Empty;
        return true;
    }

    private static bool TryAppendSubmittedDescriptorDependency_NoLock(
        VulkanResourceLifetimeTracker tracker,
        List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> touched,
        Dictionary<VulkanResourceLifetimeKey, ulong> touchedGenerations,
        VulkanResourceLifetimeKey key,
        out string failureReason)
    {
        if (!tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
        {
            failureReason = $"descriptor submission dependency {key} is no longer tracked";
            return false;
        }
        if ((resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
        {
            failureReason = $"descriptor submission dependency {key} was destroyed before submission";
            return false;
        }

        if (touchedGenerations.TryGetValue(key, out ulong trackedGeneration))
        {
            if (trackedGeneration != resource.Generation)
            {
                failureReason = $"descriptor submission dependency {key} changed generation while submission was prepared";
                return false;
            }
        }
        else
        {
            if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0 &&
                !resource.Pins.HasDescriptorReferences)
            {
                failureReason = $"descriptor submission dependency {key} began retirement before capture";
                return false;
            }
            touched.Add(new KeyValuePair<VulkanResourceLifetimeKey, ulong>(key, resource.Generation));
            touchedGenerations.Add(key, resource.Generation);
        }

        if (key.Type == ObjectType.ImageView &&
            tracker.ImageViewBackingImages.TryGetValue(key.Handle, out ulong backingImageHandle) &&
            backingImageHandle != 0 &&
            !TryAppendSubmittedDescriptorDependency_NoLock(
                tracker,
                touched,
                touchedGenerations,
                new VulkanResourceLifetimeKey(ObjectType.Image, backingImageHandle),
                out failureReason))
            return false;

        if (key.Type == ObjectType.BufferView &&
            tracker.BufferViewBackingBuffers.TryGetValue(key.Handle, out ulong backingBufferHandle) &&
            backingBufferHandle != 0 &&
            !TryAppendSubmittedDescriptorDependency_NoLock(
                tracker,
                touched,
                touchedGenerations,
                new VulkanResourceLifetimeKey(ObjectType.Buffer, backingBufferHandle),
                out failureReason))
            return false;

        failureReason = string.Empty;
        return true;
    }

    private unsafe VulkanLifetimeSubmission PublishSuccessfulSubmissionLifetime(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
    {
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        EVulkanLifetimeQueueDomain domain = ResolveLifetimeQueueDomain(queue);
        ResolveSubmissionTimelineSignal(ref submitInfo, out ulong timelineSemaphoreHandle, out ulong timelineValue);
        lock (tracker.SyncRoot)
        {
            ulong sequence = domain switch
            {
                EVulkanLifetimeQueueDomain.Graphics => ++tracker.LastGraphicsSequence,
                EVulkanLifetimeQueueDomain.Transfer => ++tracker.LastTransferSequence,
                _ => ++tracker.LastOtherSequence,
            };
            VulkanLifetimeSubmission submission = new(
                unchecked((ulong)queue.Handle),
                domain,
                sequence,
                timelineSemaphoreHandle,
                timelineValue,
                unchecked((ulong)fence.Handle));
            tracker.LifetimeSubmissions.Add(submission);

            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (handle == 0)
                    continue;
                VulkanResourceLifetimeRecord commandResource = tracker.GetOrRegisterResourceNoLock(
                    new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
                    "CommandBuffer.Submit");
                MarkSubmitted(commandResource, domain, sequence, in diagnosticContext);
                if (!tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
                    continue;
                lifetime.FrameDataLease.TryTransferToSubmission(domain, sequence);
                foreach ((VulkanResourceLifetimeKey key, _) in lifetime.TouchedDependencies)
                    if (tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                    {
                        MarkSubmitted(resource, domain, sequence, in diagnosticContext);
                        if (key.Type == ObjectType.QueryPool &&
                            tracker.RenderQueriesByPool.TryGetValue(key.Handle, out List<VkRenderQuery>? queries))
                            for (int queryIndex = 0; queryIndex < queries.Count; queryIndex++)
                                queries[queryIndex].MarkResultEpochSubmitted(handle, in submission);
                    }
            }
            return submission;
        }
    }

    internal VulkanLifetimeSubmission RecordSuccessfulSubmissionLifetime(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
        => PublishSuccessfulSubmissionLifetime(
            queue,
            ref submitInfo,
            fence,
            in diagnosticContext);

    private static void MarkSubmitted(
        VulkanResourceLifetimeRecord resource,
        EVulkanLifetimeQueueDomain domain,
        ulong sequence,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
    {
        resource.State &= ~EVulkanResourceLifetimeState.Completed;
        resource.State |= EVulkanResourceLifetimeState.Submitted;
        resource.LastSubmissionSerial = diagnosticContext.SubmissionSerial;
        resource.LastFrameOpContextId = diagnosticContext.FrameOpContextId;
        resource.LastFrameOpKind = diagnosticContext.FrameOpKind;
        resource.Pins.MarkSubmitted(domain, sequence);
    }

    private unsafe void PublishRecordedImageLayouts(
        Queue queue,
        ref SubmitInfo submitInfo,
        in VulkanLifetimeSubmission submission)
    {
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return;

        uint submissionQueueFamilyIndex = ResolveQueueFamilyIndex(queue);
        lock (Synchronization._vulkanImageLayoutLock)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                        handle,
                        out VulkanRecordedImageLayoutState? recorded))
                {
                    continue;
                }
                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in
                         recorded.TouchedSubresources)
                {
                    ulong generation = ResourceRuntime.GetPublishedGeneration(ObjectType.Image, pair.Key.ImageHandle);
                    if (pair.Value.ResourceGeneration != 0 && generation != pair.Value.ResourceGeneration)
                        continue;
                    if (!Synchronization._trackedImageSubresourceStates.TryGetValue(
                            pair.Key,
                            out VulkanImageSubresourceState? state))
                    {
                        state = new VulkanImageSubresourceState();
                        Synchronization._trackedImageSubresourceStates.Add(pair.Key, state);
                    }
                    VulkanImageAccessState publishedState = pair.Value;
                    if (TryResolveQueueOwnershipTransfer(
                            recorded,
                            pair.Key,
                            submissionQueueFamilyIndex,
                            out VulkanQueueOwnershipTransferRequirement ownershipRequirement,
                            out EVulkanQueueOwnershipTransferRole ownershipRole))
                    {
                        if (ownershipRole == EVulkanQueueOwnershipTransferRole.Release)
                        {
                            publishedState = publishedState with
                            {
                                QueueFamilyIndex = ownershipRequirement.SourceQueueFamilyIndex,
                            };
                            state.PendingQueueOwnershipRelease = new VulkanPendingQueueOwnershipRelease(
                                ownershipRequirement,
                                submission);
                        }
                        else
                        {
                            state.PendingQueueOwnershipRelease = null;
                        }
                    }

                    state.Submitted = publishedState;
                    if (publishedState.ExternalOwnership != EVulkanExternalImageOwnership.EngineOwned)
                        Synchronization._externalImageOwnershipByHandle[pair.Key.ImageHandle] =
                            (publishedState.ResourceGeneration, publishedState.ExternalOwnership);
                    switch (submission.QueueDomain)
                    {
                        case EVulkanLifetimeQueueDomain.Graphics:
                            state.GraphicsSequence = Math.Max(state.GraphicsSequence, submission.QueueSequence);
                            break;
                        case EVulkanLifetimeQueueDomain.Transfer:
                            state.TransferSequence = Math.Max(state.TransferSequence, submission.QueueSequence);
                            break;
                        default:
                            state.OtherSequence = Math.Max(state.OtherSequence, submission.QueueSequence);
                            break;
                    }
                }
            }
        }
    }

    private static bool TryResolveQueueOwnershipTransfer(
        VulkanRecordedImageLayoutState recorded,
        VulkanTrackedImageSubresource key,
        uint submissionQueueFamilyIndex,
        out VulkanQueueOwnershipTransferRequirement requirement,
        out EVulkanQueueOwnershipTransferRole role)
    {
        for (int transferIndex = recorded.QueueOwnershipTransfers.Count - 1; transferIndex >= 0; transferIndex--)
        {
            VulkanQueueOwnershipTransferRequirement candidate = recorded.QueueOwnershipTransfers[transferIndex];
            EVulkanQueueOwnershipTransferRole candidateRole = candidate.ResolveRole(submissionQueueFamilyIndex);
            if (candidateRole == EVulkanQueueOwnershipTransferRole.Invalid ||
                !candidate.Contains(key.ImageHandle, key.MipLevel, key.ArrayLayer, key.Aspect))
                continue;

            requirement = candidate;
            role = candidateRole;
            return true;
        }

        requirement = default;
        role = EVulkanQueueOwnershipTransferRole.Invalid;
        return false;
    }

    private unsafe void ReleaseSubmissionLifetimePins(ref SubmitInfo submitInfo)
    {
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (handle == 0)
                    continue;
                if (tracker.ResourceLifetimes.TryGetValue(
                        new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
                        out VulkanResourceLifetimeRecord? commandResource))
                {
                    ReleaseQueuedPin(commandResource);
                }
                if (tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
                    foreach ((VulkanResourceLifetimeKey key, _) in lifetime.TouchedDependencies)
                        if (tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                            ReleaseQueuedPin(resource);
            }
        }
        ReleaseSubmissionGatewayPins(ref submitInfo);
    }

    internal void ReleaseSubmissionResourceLifetimePins(ref SubmitInfo submitInfo)
        => ReleaseSubmissionLifetimePins(ref submitInfo);

    private static void ReleaseQueuedPin(VulkanResourceLifetimeRecord resource)
    {
        resource.Pins.ReleaseQueuedReference();
        if (!resource.Pins.HasQueuedReferences)
            resource.State &= ~EVulkanResourceLifetimeState.Queued;
    }

    private unsafe void ReleaseSubmissionGatewayPins(ref SubmitInfo submitInfo)
    {
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (handle == 0 ||
                    !tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
                {
                    continue;
                }
                if (lifetime.QueuedSubmissionCount <= 0)
                    throw new InvalidOperationException($"Command buffer 0x{handle:X} submission gateway pin underflow.");
                lifetime.QueuedSubmissionCount--;
                lifetime.FrameDataLease.CompleteRecording(cacheVariant: true);
                if (CommandBuffers.TrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
                    lock (batch)
                    {
                        if (batch.QueuedSubmissionCount <= 0)
                            throw new InvalidOperationException($"Command buffer 0x{handle:X} tracking gateway pin underflow.");
                        batch.QueuedSubmissionCount--;
                    }
            }
        }
    }

    internal unsafe void ResolveSubmissionMarkers(ref SubmitInfo submitInfo, bool submissionSucceeded)
    {
        ResolveSubmissionTimelineSignal(ref submitInfo, out ulong semaphoreHandle, out ulong timelineValue);
        lock (Synchronization._submissionMarkerLock)
        {
            for (uint commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                nint handle = submitInfo.PCommandBuffers[commandIndex].Handle;
                if (!Synchronization._submissionMarkersByCommandBuffer.TryGetValue(
                        handle,
                        out List<VulkanTimelineGpuFence>? markers))
                {
                    continue;
                }
                bool canBind = submissionSucceeded && semaphoreHandle != 0 && timelineValue != 0;
                for (int markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                    if (canBind)
                        markers[markerIndex].Bind(semaphoreHandle, timelineValue);
                    else
                        markers[markerIndex].Fail();
                markers.Clear();
            }
        }
    }

    private static unsafe TimelineSemaphoreSubmitInfo* FindTrackedTimelineInfo(void* pNext)
        => FindTrackedTimelineInfo((BaseInStructure*)pNext, remainingNodes: 64);

    private static unsafe TimelineSemaphoreSubmitInfo* FindTrackedTimelineInfo(
        BaseInStructure* current,
        int remainingNodes)
    {
        if (current is null || remainingNodes <= 0)
            return null;
        if (current->SType == StructureType.TimelineSemaphoreSubmitInfo)
            return (TimelineSemaphoreSubmitInfo*)current;
        return FindTrackedTimelineInfo(current->PNext, remainingNodes - 1);
    }

    private unsafe void ResolveSubmissionTimelineSignal(
        ref SubmitInfo submitInfo,
        out ulong semaphoreHandle,
        out ulong timelineValue)
    {
        semaphoreHandle = 0;
        timelineValue = 0;
        TimelineSemaphoreSubmitInfo* timeline = FindTrackedTimelineInfo(submitInfo.PNext);
        if (timeline is null || timeline->PSignalSemaphoreValues is null || submitInfo.PSignalSemaphores is null)
            return;
        uint count = Math.Min(timeline->SignalSemaphoreValueCount, submitInfo.SignalSemaphoreCount);
        ReadOnlySpan<ulong> signalValues = new(
            timeline->PSignalSemaphoreValues,
            checked((int)count));
        ReadOnlySpan<Semaphore> signalSemaphores = new(
            submitInfo.PSignalSemaphores,
            checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            ulong value = signalValues[(int)index];
            Semaphore semaphore = signalSemaphores[(int)index];
            if (value == 0 || semaphore.Handle == 0)
                continue;
            semaphoreHandle = semaphore.Handle;
            timelineValue = value;
            return;
        }
    }

    private EVulkanLifetimeQueueDomain ResolveLifetimeQueueDomain(Queue queue)
    {
        if (queue.Handle == DeviceContext.GraphicsQueue.Handle ||
            queue.Handle == DeviceContext.SecondaryGraphicsQueue.Handle)
        {
            return EVulkanLifetimeQueueDomain.Graphics;
        }
        if (queue.Handle == DeviceContext.TransferQueue.Handle &&
            queue.Handle != DeviceContext.GraphicsQueue.Handle)
        {
            return EVulkanLifetimeQueueDomain.Transfer;
        }
        return EVulkanLifetimeQueueDomain.Other;
    }

    private unsafe Result SubmitNative(Queue queue, ref SubmitInfo submitInfo, Fence fence)
    {
        if (Synchronization._activeSynchronizationBackend != EVulkanSynchronizationBackend.Sync2)
            return Api.QueueSubmit(queue, 1, ref submitInfo, fence);

        int waitCount = checked((int)submitInfo.WaitSemaphoreCount);
        int signalCount = checked((int)submitInfo.SignalSemaphoreCount);
        int commandCount = checked((int)submitInfo.CommandBufferCount);
        VulkanSynchronizationThreadState scratch =
            Synchronization._synchronizationThreadWorkspace.Current;
        using VulkanNativeScratchReservation<SemaphoreSubmitInfo> waitReservation =
            scratch.SubmitWaitInfoScratch.Reserve(waitCount);
        using VulkanNativeScratchReservation<SemaphoreSubmitInfo> signalReservation =
            scratch.SubmitSignalInfoScratch.Reserve(signalCount);
        using VulkanNativeScratchReservation<CommandBufferSubmitInfo> commandReservation =
            scratch.SubmitCommandBufferInfoScratch.Reserve(commandCount);
        Span<SemaphoreSubmitInfo> waits = waitReservation.Span;
        Span<SemaphoreSubmitInfo> signals = signalReservation.Span;
        Span<CommandBufferSubmitInfo> commands = commandReservation.Span;
        TimelineSemaphoreSubmitInfo* timeline = FindTrackedTimelineInfo(submitInfo.PNext);
        ReadOnlySpan<Semaphore> nativeWaitSemaphores = new(
            submitInfo.PWaitSemaphores,
            waitCount);
        ReadOnlySpan<PipelineStageFlags> nativeWaitStages = new(
            submitInfo.PWaitDstStageMask,
            waitCount);
        ReadOnlySpan<ulong> nativeWaitValues = timeline is not null &&
            timeline->PWaitSemaphoreValues is not null
                ? new ReadOnlySpan<ulong>(
                    timeline->PWaitSemaphoreValues,
                    Math.Min(waitCount, checked((int)timeline->WaitSemaphoreValueCount)))
                : default;
        ReadOnlySpan<Semaphore> nativeSignalSemaphores = new(
            submitInfo.PSignalSemaphores,
            signalCount);
        ReadOnlySpan<ulong> nativeSignalValues = timeline is not null &&
            timeline->PSignalSemaphoreValues is not null
                ? new ReadOnlySpan<ulong>(
                    timeline->PSignalSemaphoreValues,
                    Math.Min(signalCount, checked((int)timeline->SignalSemaphoreValueCount)))
                : default;
        ReadOnlySpan<CommandBuffer> nativeCommands = new(
            submitInfo.PCommandBuffers,
            commandCount);
        for (int index = 0; index < waitCount; index++)
            waits[index] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = nativeWaitSemaphores[index],
                Value = index < nativeWaitValues.Length
                    ? nativeWaitValues[index]
                    : 0,
                StageMask = (PipelineStageFlags2)(ulong)(nativeWaitStages[index] == 0
                    ? PipelineStageFlags.AllCommandsBit
                    : nativeWaitStages[index]),
            };
        for (int index = 0; index < signalCount; index++)
            signals[index] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = nativeSignalSemaphores[index],
                Value = index < nativeSignalValues.Length
                    ? nativeSignalValues[index]
                    : 0,
                StageMask = commandCount == 0 ? PipelineStageFlags2.TopOfPipeBit : PipelineStageFlags2.AllCommandsBit,
            };
        for (int index = 0; index < commandCount; index++)
            commands[index] = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = nativeCommands[index],
            };

        fixed (SemaphoreSubmitInfo* waitPtr = waits)
        fixed (SemaphoreSubmitInfo* signalPtr = signals)
        fixed (CommandBufferSubmitInfo* commandPtr = commands)
        {
            SubmitInfo2 submit2 = new()
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = (uint)waitCount,
                PWaitSemaphoreInfos = waitCount == 0 ? null : waitPtr,
                CommandBufferInfoCount = (uint)commandCount,
                PCommandBufferInfos = commandCount == 0 ? null : commandPtr,
                SignalSemaphoreInfoCount = (uint)signalCount,
                PSignalSemaphoreInfos = signalCount == 0 ? null : signalPtr,
            };
            if (DeviceContext.InstanceApiVersion >= Vk.Version13)
                return Api.QueueSubmit2(queue, 1, &submit2, fence);
            if (DeviceContext.ExtensionFunctions.KhrSynchronization2 is not { } synchronization2)
                return Result.ErrorExtensionNotPresent;
            return synchronization2.QueueSubmit2(queue, 1, &submit2, fence);
        }
    }

}
