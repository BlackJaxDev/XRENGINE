using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanImageAccessRangeDelta(
        ulong ImageHandle,
        ImageSubresourceRange Range,
        VulkanImageAccessState State);

    internal sealed class VulkanCommandBufferImageAccessIndex(int initialCapacity = 32)
    {
        private readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> _states = new Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState>(initialCapacity);

        public int Count => _states.Count;

        public void Clear()
            => _states.Clear();

        public void Record(
            ulong imageHandle,
            in ImageSubresourceRange range,
            in VulkanImageAccessState state)
        {
            uint levelCount = Math.Max(range.LevelCount, 1u);
            uint layerCount = Math.Max(range.LayerCount, 1u);
            for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
            {
                uint mip = range.BaseMipLevel + mipOffset;
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint layer = range.BaseArrayLayer + layerOffset;
                    RecordAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, state);
                    RecordAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, state);
                    RecordAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, state);
                }
            }
        }

        public bool TryGet(
            ulong imageHandle,
            in ImageSubresourceRange range,
            out VulkanImageAccessState state)
        {
            VulkanImageAccessState? combined = null;
            uint levelCount = Math.Max(range.LevelCount, 1u);
            uint layerCount = Math.Max(range.LayerCount, 1u);
            for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
            {
                uint mip = range.BaseMipLevel + mipOffset;
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint layer = range.BaseArrayLayer + layerOffset;
                    if (!TryMergeAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, ref combined) ||
                        !TryMergeAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, ref combined) ||
                        !TryMergeAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, ref combined))
                    {
                        state = VulkanImageAccessState.Undefined;
                        return false;
                    }
                }
            }

            state = combined ?? VulkanImageAccessState.Undefined;
            return combined.HasValue;
        }

        private void RecordAspect(
            ulong imageHandle,
            uint mip,
            uint layer,
            ImageAspectFlags rangeAspect,
            ImageAspectFlags trackedAspect,
            in VulkanImageAccessState state)
        {
            if ((rangeAspect & trackedAspect) == 0)
                return;

            _states[new VulkanTrackedImageSubresource(imageHandle, mip, layer, trackedAspect)] = state;
        }

        private bool TryMergeAspect(
            ulong imageHandle,
            uint mip,
            uint layer,
            ImageAspectFlags rangeAspect,
            ImageAspectFlags trackedAspect,
            ref VulkanImageAccessState? combined)
        {
            if ((rangeAspect & trackedAspect) == 0)
                return true;

            VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
            if (!_states.TryGetValue(key, out VulkanImageAccessState current) ||
                current.Layout == ImageLayout.Undefined)
                return false;

            if (!combined.HasValue)
            {
                combined = current;
                return true;
            }

            VulkanImageAccessState prior = combined.Value;
            if (prior.Layout != current.Layout ||
                (prior.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                 current.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                 prior.QueueFamilyIndex != current.QueueFamilyIndex))
                return false;

            combined = prior with
            {
                StageMask = prior.StageMask | current.StageMask,
                AccessMask = prior.AccessMask | current.AccessMask,
                QueueFamilyIndex = prior.QueueFamilyIndex != Vk.QueueFamilyIgnored
                    ? prior.QueueFamilyIndex
                    : current.QueueFamilyIndex,
                ExpectedDescriptorLayout = prior.ExpectedDescriptorLayout == current.ExpectedDescriptorLayout
                    ? prior.ExpectedDescriptorLayout
                    : ImageLayout.Undefined,
                Serial = Math.Max(prior.Serial, current.Serial),
            };
            return true;
        }
    }

    private sealed class VulkanCommandBufferTrackingBatch
    {
        public readonly HashSet<VulkanResourceLifetimeKey> Dependencies = new(64);
        public readonly Dictionary<ulong, ulong> ExpandedDescriptorGenerations = new(8);
        public readonly Dictionary<ulong, (ulong DescriptorGeneration, ulong LayoutVersion)> ValidatedDescriptorGenerations = new(8);
        public readonly List<VulkanImageAccessRangeDelta> ImageAccessDeltas = new(32);
        public readonly VulkanCommandBufferImageAccessIndex LatestImageAccessStates = new(32);
        public ulong RecordingGeneration;
        public ulong LayoutVersion;
        public int DependencyBindCount;
        public int ImageAccessWriteCount;
        public int PublishedImageDeltaCount;
        public int ReportedDependencyBindCount;
        public int ReportedImageAccessWriteCount;
        public int QueuedSubmissionCount;
        public bool IsRecording;

        public void Reset(ulong recordingGeneration)
        {
            Dependencies.Clear();
            ExpandedDescriptorGenerations.Clear();
            ValidatedDescriptorGenerations.Clear();
            ImageAccessDeltas.Clear();
            LatestImageAccessStates.Clear();
            RecordingGeneration = recordingGeneration;
            LayoutVersion = 0;
            DependencyBindCount = 0;
            ImageAccessWriteCount = 0;
            PublishedImageDeltaCount = 0;
            ReportedDependencyBindCount = 0;
            ReportedImageAccessWriteCount = 0;
            QueuedSubmissionCount = 0;
            IsRecording = true;
        }

        public void RecordDependency(VulkanResourceLifetimeKey key)
        {
            DependencyBindCount++;
            Dependencies.Add(key);
        }

        public bool MarkDescriptorExpanded(ulong descriptorSetHandle, ulong descriptorGeneration)
        {
            if (ExpandedDescriptorGenerations.TryGetValue(descriptorSetHandle, out ulong existing) &&
                existing == descriptorGeneration)
            {
                return false;
            }

            ExpandedDescriptorGenerations[descriptorSetHandle] = descriptorGeneration;
            return true;
        }

        public bool MarkDescriptorValidated(ulong descriptorSetHandle, ulong descriptorGeneration)
        {
            (ulong DescriptorGeneration, ulong LayoutVersion) key = (descriptorGeneration, LayoutVersion);
            if (ValidatedDescriptorGenerations.TryGetValue(descriptorSetHandle, out var existing) && existing == key)
                return false;

            ValidatedDescriptorGenerations[descriptorSetHandle] = key;
            return true;
        }

        public void RecordImageAccess(in VulkanImageAccessRangeDelta delta)
        {
            ImageAccessWriteCount++;
            LayoutVersion++;
            LatestImageAccessStates.Record(delta.ImageHandle, delta.Range, delta.State);
            if (ImageAccessDeltas.Count > PublishedImageDeltaCount)
            {
                VulkanImageAccessRangeDelta previous = ImageAccessDeltas[^1];
                if (previous.ImageHandle == delta.ImageHandle &&
                    previous.State.Layout == delta.State.Layout &&
                    previous.State.StageMask == delta.State.StageMask &&
                    previous.State.AccessMask == delta.State.AccessMask &&
                    previous.State.QueueFamilyIndex == delta.State.QueueFamilyIndex &&
                    previous.State.ResourceGeneration == delta.State.ResourceGeneration &&
                    TryMergeRanges(previous.Range, delta.Range, out ImageSubresourceRange mergedRange))
                {
                    ImageAccessDeltas[^1] = delta with { Range = mergedRange };
                    return;
                }
            }

            ImageAccessDeltas.Add(delta);
        }

        private static bool SameRange(in ImageSubresourceRange left, in ImageSubresourceRange right)
            => left.AspectMask == right.AspectMask &&
               left.BaseMipLevel == right.BaseMipLevel &&
               left.LevelCount == right.LevelCount &&
               left.BaseArrayLayer == right.BaseArrayLayer &&
               left.LayerCount == right.LayerCount;

        private static bool TryMergeRanges(
            in ImageSubresourceRange left,
            in ImageSubresourceRange right,
            out ImageSubresourceRange merged)
        {
            if (SameRange(left, right))
            {
                merged = right;
                return true;
            }

            if (left.AspectMask == right.AspectMask &&
                left.BaseArrayLayer == right.BaseArrayLayer &&
                left.LayerCount == right.LayerCount &&
                left.BaseMipLevel + Math.Max(left.LevelCount, 1u) == right.BaseMipLevel)
            {
                merged = left with { LevelCount = Math.Max(left.LevelCount, 1u) + Math.Max(right.LevelCount, 1u) };
                return true;
            }

            if (left.AspectMask == right.AspectMask &&
                left.BaseMipLevel == right.BaseMipLevel &&
                left.LevelCount == right.LevelCount &&
                left.BaseArrayLayer + Math.Max(left.LayerCount, 1u) == right.BaseArrayLayer)
            {
                merged = left with { LayerCount = Math.Max(left.LayerCount, 1u) + Math.Max(right.LayerCount, 1u) };
                return true;
            }

            merged = default;
            return false;
        }
    }

    private readonly ConcurrentDictionary<ulong, VulkanCommandBufferTrackingBatch> _commandBufferTrackingBatches = new();

    private void BeginCommandBufferTrackingBatch(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        ulong recordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer);
        lock (_vulkanResourceLifetimeLock)
        {
            if (_vulkanCommandBufferLifetimes.TryGetValue(
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

        lock (_vulkanResourceLifetimeLock)
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
            VulkanImageAccessState resolved = ResolveVulkanImageAccessState(
                layout,
                primaryAspect,
                queueFamilyIndex,
                serial,
                GetCurrentVulkanResourceGeneration(ObjectType.Image, image.Handle));
            resolved = resolved with
            {
                StageMask = stageMask == 0 ? resolved.StageMask : NormalizePipelineStages2(stageMask),
                AccessMask = NormalizeAccessFlags2(accessMask),
            };
            batch.RecordImageAccess(new VulkanImageAccessRangeDelta(image.Handle, range, resolved));
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
    private Result EndCommandBufferTracked(
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

        lock (_vulkanResourceLifetimeLock)
        {
            if (_commandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            {
                lock (batch)
                    batch.IsRecording = false;
            }

            if (_vulkanCommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                if (result == Result.Success && trackingPublished)
                    lifetime.FrameDataLease.CompleteRecording(cacheVariant);
                else
                    lifetime.FrameDataLease.AbandonRecording();
            }
        }

        return result;
    }

    private bool TryFlushCommandBufferTrackingBatch(CommandBuffer commandBuffer, out string failureReason)
    {
        failureReason = string.Empty;
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0 || !_commandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            return true;

        if (batch.Dependencies.Count == 0 &&
            batch.PublishedImageDeltaCount == batch.ImageAccessDeltas.Count)
        {
            return true;
        }

        int newUniqueDependencies = batch.Dependencies.Count;
        int newCompactImageRanges = batch.ImageAccessDeltas.Count - batch.PublishedImageDeltaCount;

        bool lifetimeLockContended = !Monitor.TryEnter(_vulkanResourceLifetimeLock);
        if (lifetimeLockContended)
            Monitor.Enter(_vulkanResourceLifetimeLock);
        try
        {
            if (!_vulkanCommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                _vulkanCommandBufferLifetimes[handle] = lifetime;
            }

            foreach (VulkanResourceLifetimeKey key in batch.Dependencies)
            {
                if (!TryTrackVulkanCommandBufferResource_NoLock(
                    handle,
                    key,
                    "CommandBuffer.LocalBatch",
                    out failureReason,
                    allowQueuedSubmission: true))
                {
                    return false;
                }
            }

            lifetime.RefreshTouchedDependencies();
            batch.Dependencies.Clear();
        }
        finally
        {
            Monitor.Exit(_vulkanResourceLifetimeLock);
        }

        bool layoutLockContended = FlushCommandBufferImageAccessBatch(commandBuffer, batch);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingBatch(
            dependencyBinds: batch.DependencyBindCount - batch.ReportedDependencyBindCount,
            uniqueDependencies: newUniqueDependencies,
            imageAccessWrites: batch.ImageAccessWriteCount - batch.ReportedImageAccessWriteCount,
            compactImageRanges: newCompactImageRanges);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingContention(
            lifetimeLockContended ? 1 : 0,
            layoutLockContended ? 1 : 0);
        batch.ReportedDependencyBindCount = batch.DependencyBindCount;
        batch.ReportedImageAccessWriteCount = batch.ImageAccessWriteCount;
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
                lock (_vulkanResourceLifetimeLock)
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
