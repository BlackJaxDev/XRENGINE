using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{



    private readonly ConcurrentDictionary<ulong, VulkanCommandBufferTrackingBatch> _commandBufferTrackingBatches = new();

    private void BeginCommandBufferTrackingBatch(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        ulong recordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer);
        lock (_resourceLifetimeTracker.SyncRoot)
        {
            if (_resourceLifetimeTracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.QueuedSubmissionCount != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{handle:X} cannot begin recording while queued for submission.");
            }

            if (_commandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? existing))
            {
                lock (existing)
                {
                    if (existing.QueuedSubmissionCount != 0)
                    {
                        throw new InvalidOperationException(
                            $"Command buffer 0x{handle:X} cannot replace tracking while queued for submission.");
                    }

                    existing.Reset(recordingGeneration);
                    return;
                }
            }

            VulkanCommandBufferTrackingBatch batch = new();
            batch.Reset(recordingGeneration);
            _commandBufferTrackingBatches[handle] = batch;
        }
    }

    private void RemoveCommandBufferTrackingBatch(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        lock (_resourceLifetimeTracker.SyncRoot)
        {
            if (!_commandBufferTrackingBatches.TryGetValue(
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

                _commandBufferTrackingBatches.TryRemove(handle, out _);
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
        lock (_resourceLifetimeTracker.SyncRoot)
        {
            if (!_commandBufferTrackingBatches.TryGetValue(
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
                _commandBufferTrackingBatches.TryRemove(handle, out _);
                abandoned = true;
            }

            if (_resourceLifetimeTracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime.FrameDataLease.AbandonRecording();
                ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
            }
        }

        if (abandoned)
            ResetRecordedImageLayoutState(commandBuffer);
        return abandoned;
    }

    private bool TryRecordCommandBufferDependency(CommandBuffer commandBuffer, ObjectType type, ulong handle)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || handle == 0)
            return false;

        if (!_commandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out var currentBatch) ||
                !ReferenceEquals(batch, currentBatch))
            {
                return false;
            }
            if (batch.QueuedSubmissionCount != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record resource dependencies while queued for submission.");
            }

            batch.RecordDependency(ResourceKey(type, handle));
            return true;
        }
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

        if (!_commandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out var currentBatch) ||
                !ReferenceEquals(batch, currentBatch))
            {
                return false;
            }
            if (batch.QueuedSubmissionCount != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record image access while queued for submission.");
            }

            ImageAspectFlags primaryAspect = (range.AspectMask & ImageAspectFlags.ColorBit) != 0
                ? ImageAspectFlags.ColorBit
                : (range.AspectMask & ImageAspectFlags.DepthBit) != 0
                    ? ImageAspectFlags.DepthBit
                    : ImageAspectFlags.StencilBit;
            ulong serial = unchecked((ulong)Interlocked.Increment(ref _vulkanImageLayoutTransitionSerial));
            VulkanImageAccessState resolved = ResolveRecordedVulkanImageAccessState(
                layout,
                primaryAspect,
                stageMask,
                accessMask,
                queueFamilyIndex,
                serial,
                GetCurrentVulkanResourceGeneration(ObjectType.Image, image.Handle));
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
                    ExternalOwnership = ResolveTrackedExternalImageOwnership(
                        image,
                        range,
                        resolved.ResourceGeneration),
                };
            }

            batch.RecordImageAccess(new VulkanImageAccessRangeDelta(image.Handle, range, resolved));
            return true;
        }
    }

    private bool TryRecordQueueOwnershipTransferRequirement(
        CommandBuffer commandBuffer,
        in VulkanQueueOwnershipTransferRequirement requirement)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || !requirement.IsValid)
            return false;

        if (!_commandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out var currentBatch) ||
                !ReferenceEquals(batch, currentBatch))
            {
                return false;
            }
            if (batch.QueuedSubmissionCount != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record queue-ownership requirements while queued for submission.");
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

        if (!_commandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!_commandBufferTrackingBatches.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferTrackingBatch? currentBatch) ||
                !ReferenceEquals(batch, currentBatch) ||
                batch.QueuedSubmissionCount != 0 ||
                !batch.LatestImageAccessStates.TryGet(
                    image.Handle,
                    range,
                    out VulkanImageAccessState prior))
            {
                return false;
            }

            ulong serial = unchecked((ulong)Interlocked.Increment(
                ref _vulkanImageLayoutTransitionSerial));
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

        if (!_commandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        lock (batch)
        {
            if (!_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out var currentBatch) ||
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
    private Result EndCommandBufferTracked(CommandBuffer commandBuffer, bool cacheVariant = true)
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
        lock (_resourceLifetimeTracker.SyncRoot)
        {
            if (_commandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            {
                lock (batch)
                    batch.IsRecording = false;
            }

            if (_resourceLifetimeTracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                if (result == Result.Success && trackingPublished)
                    lifetime.FrameDataLease.CompleteRecording(cacheVariant);
                else
                {
                    lifetime.FrameDataLease.AbandonRecording();
                    ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
                }
            }

            if (discarded)
                _commandBufferTrackingBatches.TryRemove(handle, out _);
        }

        if (discarded)
            ResetRecordedImageLayoutState(commandBuffer);

        return result;
    }

    private bool TryFlushCommandBufferTrackingBatch(CommandBuffer commandBuffer, out string failureReason)
    {
        failureReason = string.Empty;
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0 || !_commandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            return true;

        int newUniqueDependencies = 0;
        int newCompactImageRanges = 0;
        bool lifetimeLockContended = !Monitor.TryEnter(_resourceLifetimeTracker.SyncRoot);
        if (lifetimeLockContended)
            Monitor.Enter(_resourceLifetimeTracker.SyncRoot);
        try
        {
            lock (batch)
            {
                if (!_commandBufferTrackingBatches.TryGetValue(
                        handle,
                        out VulkanCommandBufferTrackingBatch? currentBatch) ||
                    !ReferenceEquals(batch, currentBatch))
                {
                    return true;
                }

                if (batch.Dependencies.Count == 0 &&
                    batch.PublishedImageDeltaCount == batch.ImageAccessDeltas.Count)
                {
                    return true;
                }

                newUniqueDependencies = batch.Dependencies.Count;
                newCompactImageRanges =
                    batch.ImageAccessDeltas.Count - batch.PublishedImageDeltaCount;

                if (!_resourceLifetimeTracker.CommandBufferLifetimes.TryGetValue(
                        handle,
                        out VulkanCommandBufferLifetimeRecord? lifetime))
                {
                    lifetime = new VulkanCommandBufferLifetimeRecord();
                    _resourceLifetimeTracker.CommandBufferLifetimes[handle] = lifetime;
                }

                foreach (VulkanResourceLifetimeKey key in batch.Dependencies)
                {
                    if (!TryValidateVulkanCommandBufferResource_NoLock(
                            handle,
                            key,
                            "CommandBuffer.LocalBatch",
                            out failureReason,
                            allowQueuedSubmission: true))
                    {
                        return false;
                    }
                }

                foreach (VulkanResourceLifetimeKey key in batch.Dependencies)
                {
                    // Validation and publication occur under the same lifetime lock;
                    // this second pass therefore cannot partially fail.
                    if (!TryTrackVulkanCommandBufferResource_NoLock(
                            handle,
                            key,
                            "CommandBuffer.LocalBatch",
                            out failureReason,
                            allowQueuedSubmission: true))
                    {
                        throw new InvalidOperationException(
                            $"Validated Vulkan dependency {key} failed transactional publication: {failureReason}");
                    }
                }

                lifetime.RefreshTouchedDependencies();
                batch.Dependencies.Clear();
            }
        }
        finally
        {
            Monitor.Exit(_resourceLifetimeTracker.SyncRoot);
        }

        bool layoutLockContended;
        int dependencyBinds;
        int imageAccessWrites;
        lock (batch)
        {
            layoutLockContended = FlushCommandBufferImageAccessBatch(commandBuffer, batch);
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
    private void PublishCommandBufferTrackingDependenciesBeforeResourceRetirement(
        VulkanResourceLifetimeKey resourceKey)
    {
        List<ulong>? pendingCommandBuffers = null;
        foreach (KeyValuePair<ulong, VulkanCommandBufferTrackingBatch> pair in _commandBufferTrackingBatches)
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
                _ = InvalidateCachedCommandBuffersByHandle(
                    [commandBufferHandle],
                    $"retirement dependency publication rejected: {failureReason}");

                // The batch can no longer be submitted: one of its dependencies crossed
                // retirement before the deferred publication completed. Discarding only
                // this invalid batch closes the retirement race and lets the next frame
                // record against the replacement resource generation.
                lock (_resourceLifetimeTracker.SyncRoot)
                {
                    if (_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out VulkanCommandBufferTrackingBatch? batch))
                    {
                        lock (batch)
                        {
                            if (batch.QueuedSubmissionCount == 0)
                                _commandBufferTrackingBatches.TryRemove(commandBufferHandle, out _);
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
