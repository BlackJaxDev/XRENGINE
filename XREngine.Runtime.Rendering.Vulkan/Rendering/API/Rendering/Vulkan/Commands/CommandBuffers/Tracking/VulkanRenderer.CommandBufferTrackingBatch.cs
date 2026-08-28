using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    internal Result ResetTrackedCommandBuffer(CommandBuffer commandBuffer)
        => ResetCommandBufferWithLifetime(commandBuffer, "ResetTrackedCommandBuffer");



    private ConcurrentDictionary<ulong, VulkanCommandBufferTrackingBatch> _commandBufferTrackingBatches
        => _commandRuntime.CommandBuffers.TrackingBatches;

    private ConcurrentDictionary<ulong, VulkanCommandBufferTrackingBatch> CommandBufferTrackingBatches
        => _commandBufferTrackingBatches;

    private void RemoveCommandBufferTrackingBatch(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!CommandBufferTrackingBatches.TryGetValue(
                    handle,
                    out VulkanCommandBufferTrackingBatch? batch))
            {
                return;
            }

            lock (batch)
            {
                if (batch.QueuedSubmissionCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Command buffer 0x{handle:X} tracking cannot be removed while queued for submission.");
                }

                CommandBufferTrackingBatches.TryRemove(handle, out _);
            }
        }
    }

    /// <summary>
    /// Discards a non-pending command-buffer recording, including one that reached
    /// <see cref="EndCommandBufferTracked"/> but was rejected before submission.
    /// The engine-side batch and frame-data lease must release their recorded
    /// dependencies before the next reset attempt.
    /// </summary>
    private bool TryAbandonCommandBufferRecording(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return false;

        bool abandoned = false;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!CommandBufferTrackingBatches.TryGetValue(
                    handle,
                    out VulkanCommandBufferTrackingBatch? batch))
            {
                return false;
            }

            lock (batch)
            {
                if (batch.QueuedSubmissionCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Command buffer 0x{handle:X} recording cannot be abandoned while queued for submission.");
                }

                batch.IsRecording = false;
                CommandBufferTrackingBatches.TryRemove(handle, out _);
                abandoned = true;
            }

            ResourceRuntime.AbandonCommandBufferRecording(commandBuffer);
        }

        if (abandoned)
            ResetCommandBufferImageLayoutJournal(commandBuffer);
        return abandoned;
    }

    private bool TryRecordImageAccessDelta(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        ImageLayout layout,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || image.Handle == 0)
            return false;

        if (!CommandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!CommandBufferTrackingBatches.TryGetValue(commandBufferHandle, out var currentBatch) ||
                !ReferenceEquals(batch, currentBatch))
            {
                return false;
            }
            if (!batch.IsRecording || batch.QueuedSubmissionCount != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record image access outside an active, unqueued recording.");
            }

            ImageAspectFlags primaryAspect = (range.AspectMask & ImageAspectFlags.ColorBit) != 0
                ? ImageAspectFlags.ColorBit
                : (range.AspectMask & ImageAspectFlags.DepthBit) != 0
                    ? ImageAspectFlags.DepthBit
                    : ImageAspectFlags.StencilBit;
            ulong serial = unchecked((ulong)Interlocked.Increment(ref _frameTelemetry._vulkanImageLayoutTransitionSerial));
            VulkanImageAccessState resolved = ResolveRecordedCommandImageAccessState(
                layout,
                primaryAspect,
                stageMask,
                accessMask,
                queueFamilyIndex,
                serial,
                ResourceRuntime.GetPublishedGeneration(ObjectType.Image, image.Handle));
            if (batch.LatestImageAccessStates.TryGet(
                    image.Handle,
                    range,
                    out VulkanImageAccessState prior))
            {
                resolved = resolved with
                {
                    ExternalOwnership = prior.ExternalOwnership,
                };
            }
            else
            {
                resolved = resolved with
                {
                    ExternalOwnership = ResolveTrackedExternalImageOwnershipForRecording(
                        image,
                        range,
                        resolved.ResourceGeneration),
                };
            }

            batch.RecordImageAccess(new VulkanImageAccessRangeDelta(image.Handle, range, resolved));
            return true;
        }
    }

    private static VulkanImageAccessState ResolveRecordedCommandImageAccessState(
        ImageLayout layout,
        ImageAspectFlags aspectMask,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex,
        ulong serial,
        ulong resourceGeneration)
    {
        VulkanImageAccessState canonical = ResolveCommandImageAccessState(
            layout,
            aspectMask,
            requestedStages: 0,
            requestedAccess: 0,
            queueFamilyIndex,
            resourceGeneration);
        PipelineStageFlags2 requestedStages = (PipelineStageFlags2)(ulong)stageMask;
        AccessFlags2 requestedAccess = (AccessFlags2)(ulong)accessMask;
        if (layout == ImageLayout.General)
        {
            return canonical with
            {
                StageMask = requestedStages == 0 ? canonical.StageMask : requestedStages,
                AccessMask = requestedAccess == 0 ? canonical.AccessMask : requestedAccess,
                Serial = serial,
            };
        }

        bool stagesAreCompatible = requestedStages != 0 &&
            (requestedStages & ~canonical.StageMask) == 0;
        bool accessIsCompatible = requestedAccess != 0 &&
            (requestedAccess & ~canonical.AccessMask) == 0;
        return stagesAreCompatible && accessIsCompatible
            ? canonical with
            {
                StageMask = requestedStages,
                AccessMask = requestedAccess,
                Serial = serial,
            }
            : canonical with { Serial = serial };
    }

    private EVulkanExternalImageOwnership ResolveTrackedExternalImageOwnershipForRecording(
        Image image,
        in ImageSubresourceRange range,
        ulong resourceGeneration)
    {
        ImageAspectFlags aspect = (range.AspectMask & ImageAspectFlags.ColorBit) != 0
            ? ImageAspectFlags.ColorBit
            : (range.AspectMask & ImageAspectFlags.DepthBit) != 0
                ? ImageAspectFlags.DepthBit
                : ImageAspectFlags.StencilBit;
        VulkanTrackedImageSubresource key = new(
            image.Handle,
            range.BaseMipLevel,
            range.BaseArrayLayer,
            aspect);
        lock (Synchronization._vulkanImageLayoutLock)
        {
            if (Synchronization._trackedImageSubresourceStates.TryGetValue(
                    key,
                    out VulkanImageSubresourceState? state))
                return state.Submitted.ExternalOwnership;

            return Synchronization._externalImageOwnershipByHandle.TryGetValue(
                    image.Handle,
                    out var externalState) &&
                (externalState.ResourceGeneration == 0 ||
                 resourceGeneration == 0 ||
                 externalState.ResourceGeneration == resourceGeneration)
                    ? externalState.Ownership
                    : EVulkanExternalImageOwnership.EngineOwned;
        }
    }

    private bool TryRecordQueueOwnershipTransferRequirement(
        CommandBuffer commandBuffer,
        in VulkanQueueOwnershipTransferRequirement requirement)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || !requirement.IsValid)
            return false;

        if (!CommandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!CommandBufferTrackingBatches.TryGetValue(commandBufferHandle, out var currentBatch) ||
                !ReferenceEquals(batch, currentBatch))
            {
                return false;
            }
            if (!batch.IsRecording || batch.QueuedSubmissionCount != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record queue-ownership requirements outside an active, unqueued recording.");
            }

            batch.RecordQueueOwnershipTransfer(requirement);
            return true;
        }
    }

    private bool TryRecordExternalImageOwnershipDelta(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        EVulkanExternalImageOwnership ownership)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || image.Handle == 0)
            return false;

        if (!CommandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!CommandBufferTrackingBatches.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferTrackingBatch? currentBatch) ||
                !ReferenceEquals(batch, currentBatch) ||
                !batch.IsRecording ||
                batch.QueuedSubmissionCount != 0 ||
                !batch.LatestImageAccessStates.TryGet(
                    image.Handle,
                    range,
                    out VulkanImageAccessState prior))
            {
                return false;
            }

            ulong serial = unchecked((ulong)Interlocked.Increment(
                ref _frameTelemetry._vulkanImageLayoutTransitionSerial));
            batch.RecordImageAccess(
                new VulkanImageAccessRangeDelta(
                    image.Handle,
                    range,
                    prior with
                    {
                        ExternalOwnership = ownership,
                        Serial = serial,
                    }));
            return true;
        }
    }

    private bool TryGetPendingImageAccessState(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        out VulkanImageAccessState state)
    {
        state = VulkanImageAccessState.Undefined;
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || image.Handle == 0)
            return false;

        if (!CommandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!CommandBufferTrackingBatches.TryGetValue(commandBufferHandle, out var currentBatch) ||
                !ReferenceEquals(batch, currentBatch))
            {
                return false;
            }
            return batch.LatestImageAccessStates.TryGet(image.Handle, range, out state);
        }
    }

    private void FlushCommandBufferTrackingBatch(CommandBuffer commandBuffer)
    {
        if (!TryFlushCommandBufferTrackingBatch(commandBuffer, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    /// <summary>
    /// Ends a recording and transfers any frame-data recording lease to the cached
    /// command-buffer variant. Secondary command buffers are not submitted directly,
    /// so waiting for the submission gateway to close their recording ownership would
    /// retain one lease for every recorded secondary indefinitely.
    /// </summary>
    internal Result EndCommandBufferTracked(CommandBuffer commandBuffer, bool cacheVariant = true)
    {
        Result result = EndCommandBufferTracked(
            commandBuffer,
            cacheVariant,
            out string trackingFailure);
        if (!string.IsNullOrEmpty(trackingFailure))
            throw new InvalidOperationException(trackingFailure);
        return result;
    }

    /// <summary>
    /// Ends a recording and reports a resource-lifetime publication race without using an
    /// exception. Primary recording uses this path so it can immediately rebuild against the
    /// committed resource generation; callers for which the race is unexpected use the wrapper.
    /// </summary>
    internal Result EndCommandBufferTracked(
        CommandBuffer commandBuffer,
        bool cacheVariant,
        out string trackingFailure)
    {
        Result result = Api!.EndCommandBuffer(commandBuffer);
        trackingFailure = string.Empty;
        bool trackingPublished = result != Result.Success ||
            TryFlushCommandBufferTrackingBatch(commandBuffer, out trackingFailure);

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return result;

        bool discarded = result != Result.Success || !trackingPublished;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (CommandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            {
                lock (batch)
                    batch.IsRecording = false;
            }

            if (result == Result.Success && trackingPublished)
                ResourceRuntime.CompleteCommandBufferRecording(commandBuffer, cacheVariant);
            else
                ResourceRuntime.AbandonCommandBufferRecording(commandBuffer);

            if (discarded)
                CommandBufferTrackingBatches.TryRemove(handle, out _);
        }

        if (discarded)
            ResetCommandBufferImageLayoutJournal(commandBuffer);
        else
            TrySealRecordedGraphicsSubmissionContract(commandBuffer);

        return result;
    }

    private bool TryFlushCommandBufferTrackingBatch(CommandBuffer commandBuffer, out string failureReason)
    {
        failureReason = string.Empty;
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0 || !CommandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            return true;

        int newUniqueDependencies;
        int newCompactImageRanges;
        lock (batch)
        {
            if (!CommandBufferTrackingBatches.TryGetValue(
                    handle,
                    out VulkanCommandBufferTrackingBatch? currentBatch) ||
                !ReferenceEquals(batch, currentBatch))
                return true;

            if (batch.Dependencies.Count == 0 &&
                batch.PublishedImageDeltaCount == batch.ImageAccessDeltas.Count)
                return true;

            newUniqueDependencies = batch.Dependencies.Count;
            newCompactImageRanges = batch.ImageAccessDeltas.Count - batch.PublishedImageDeltaCount;
        }

        bool lifetimeLockContended = !Monitor.TryEnter(ResourceRuntime.Lifetime.Tracker.SyncRoot);
        if (!lifetimeLockContended)
            Monitor.Exit(ResourceRuntime.Lifetime.Tracker.SyncRoot);
        if (!ResourceRuntime.TryPublishCommandBufferTrackingBatch(commandBuffer, batch, out failureReason))
            return false;

        bool layoutLockContended;
        int dependencyBinds;
        int imageAccessWrites;
        lock (batch)
        {
            layoutLockContended = FlushImageAccessBatch(commandBuffer, batch, FrameTelemetry);
            dependencyBinds = batch.DependencyBindCount - batch.ReportedDependencyBindCount;
            imageAccessWrites = batch.ImageAccessWriteCount - batch.ReportedImageAccessWriteCount;
            batch.ReportedDependencyBindCount = batch.DependencyBindCount;
            batch.ReportedImageAccessWriteCount = batch.ImageAccessWriteCount;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingBatch(
            dependencyBinds,
            newUniqueDependencies,
            imageAccessWrites,
            newCompactImageRanges);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingContention(
            lifetimeLockContended ? 1 : 0,
            layoutLockContended ? 1 : 0);
        return true;
    }

    /// <summary>
    /// Publishes any still-local command-buffer dependencies on a resource before that
    /// resource crosses the retirement boundary. Recording batches deliberately defer
    /// lifetime-index updates to avoid taking the global lifetime lock for every Vulkan
    /// command. Destruction is the inverse, rare path and must close that publication
    /// window before it captures retirement pins; otherwise a resource used earlier in
    /// the command buffer can be destroyed while that command buffer is still recording.
    /// </summary>
    internal void PublishTrackingDependenciesBeforeResourceRetirement(
        VulkanResourceLifetimeKey resourceKey)
    {
        List<ulong>? pendingCommandBuffers = null;
        foreach (KeyValuePair<ulong, VulkanCommandBufferTrackingBatch> pair in CommandBufferTrackingBatches)
        {
            VulkanCommandBufferTrackingBatch batch = pair.Value;
            lock (batch)
            {
                if (!batch.Dependencies.Contains(resourceKey))
                {
                    continue;
                }

                (pendingCommandBuffers ??= []).Add(pair.Key);
            }
        }

        if (pendingCommandBuffers is null)
            return;

        for (int i = 0; i < pendingCommandBuffers.Count; i++)
        {
            CommandBuffer commandBuffer = new()
            {
                Handle = unchecked((nint)pendingCommandBuffers[i]),
            };
            if (!TryFlushCommandBufferTrackingBatch(commandBuffer, out string failureReason))
            {
                ulong commandBufferHandle = pendingCommandBuffers[i];
                VulkanExactInvalidationResult invalidation =
                    InvalidateCachedCommandBuffersByHandle(
                    [commandBufferHandle],
                    $"retirement dependency publication rejected: {failureReason}");
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
                    invalidation.ExactVariantsDirtied,
                    invalidation.ExactCommandChainsDirtied,
                    invalidation.UnrelatedVariantsPreserved,
                    invalidation.GlobalFallbackInvalidations);

                // The batch can no longer be submitted: one of its dependencies crossed
                // retirement before the deferred publication completed. Discarding only
                // this invalid batch closes the retirement race and lets the next frame
                // record against the replacement resource generation.
                lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
                {
                    if (CommandBufferTrackingBatches.TryGetValue(commandBufferHandle, out VulkanCommandBufferTrackingBatch? batch))
                    {
                        lock (batch)
                        {
                            if (batch.QueuedSubmissionCount == 0)
                                CommandBufferTrackingBatches.TryRemove(commandBufferHandle, out _);
                        }
                    }
                }

                Debug.VulkanWarningEvery(
                    $"Vulkan.ResourceLifetime.DiscardInvalidTrackingBatch.{commandBufferHandle}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan.ResourceLifetime] Discarded invalid command-buffer tracking batch 0x{0:X} while retiring {1}: {2}",
                    commandBufferHandle,
                    resourceKey,
                    failureReason);
            }
        }
    }
}
