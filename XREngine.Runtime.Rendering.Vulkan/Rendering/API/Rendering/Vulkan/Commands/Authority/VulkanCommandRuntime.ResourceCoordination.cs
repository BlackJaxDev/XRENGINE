using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Coordinates native resources whose creation or use must be published to
/// both the command and resource lifetime authorities.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Records one native resource dependency while closing the lock-free
    /// generation-publication race with resource retirement.
    /// </summary>
    /// <remarks>
    /// Retirement first publishes generation zero and then scans recording
    /// batches. A stable non-zero generation therefore proves either that the
    /// dependency was visible to that scan or that retirement has not started.
    /// A changing/zero generation takes the tracker-and-batch slow path so the
    /// dependency is published before retirement commits or the command is
    /// rejected before the native Vulkan command is emitted.
    /// </remarks>
    internal void TrackCommandBufferResource(
        CommandBuffer commandBuffer,
        VulkanResourceLifetimeKey resourceKey,
        string owner)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || !resourceKey.IsValid ||
            !CommandBuffers.TrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return;
        }

        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        ulong observedGeneration = tracker.GetPublishedGeneration(resourceKey);
        lock (batch)
        {
            ThrowIfCommandBufferCannotRecordDependency(
                commandBufferHandle,
                batch,
                owner);
            batch.RecordDependency(resourceKey);
        }

        if (observedGeneration != 0 &&
            tracker.GetPublishedGeneration(resourceKey) == observedGeneration)
        {
            LinkNativeCommandArtifactDependency(commandBufferHandle, resourceKey, owner);
            return;
        }

        PublishCommandBufferDependencyAfterGenerationRace(
            commandBufferHandle,
            batch,
            resourceKey,
            observedGeneration,
            owner);
        LinkNativeCommandArtifactDependency(commandBufferHandle, resourceKey, owner);
    }

    /// <summary>
    /// Publishes native render-pass ownership separately from the general
    /// lifetime batch so render-pass replacement dirties only the command
    /// artifacts that actually recorded that pass.
    /// </summary>
    private void LinkNativeCommandArtifactDependency(
        ulong commandBufferHandle,
        in VulkanResourceLifetimeKey resourceKey,
        string owner)
    {
        if (resourceKey.Type != ObjectType.RenderPass)
            return;

        VulkanNativeDependencyGraph graph = ResourceRuntime.NativeDependencies;
        if (!graph.TryGet(
                EVulkanNativeDependencyOwner.RenderPass,
                resourceKey.Handle,
                out VulkanNativeDependencyHandle renderPass) ||
            !graph.TryGet(
                EVulkanNativeDependencyOwner.CommandArtifact,
                commandBufferHandle,
                out VulkanNativeDependencyHandle commandArtifact) ||
            !graph.Link(
                EVulkanNativeDependencyOwner.RenderPass,
                renderPass,
                EVulkanNativeDependencyOwner.CommandArtifact,
                commandArtifact))
        {
            throw new InvalidOperationException(
                $"{owner} could not publish RenderPass 0x{resourceKey.Handle:X} -> CommandArtifact 0x{commandBufferHandle:X}.");
        }
    }

    /// <summary>
    /// Records executed secondary command buffers in bounded, allocation-free
    /// batches while preserving the same generation handshake as a single bind.
    /// </summary>
    internal void TrackExecutedCommandBuffers(
        CommandBuffer primary,
        ReadOnlySpan<CommandBuffer> secondaries,
        string owner)
    {
        ulong primaryHandle = unchecked((ulong)primary.Handle);
        if (primaryHandle == 0 || secondaries.IsEmpty ||
            !CommandBuffers.TrackingBatches.TryGetValue(
                primaryHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return;
        }

        const int ChunkSize = 64;
        Span<ulong> handles = stackalloc ulong[ChunkSize];
        Span<ulong> observedGenerations = stackalloc ulong[ChunkSize];
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        for (int offset = 0; offset < secondaries.Length; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, secondaries.Length - offset);
            for (int index = 0; index < count; index++)
            {
                ulong handle = unchecked((ulong)secondaries[offset + index].Handle);
                handles[index] = handle;
                observedGenerations[index] = handle == 0
                    ? 0
                    : tracker.GetPublishedGeneration(
                        new VulkanResourceLifetimeKey(
                            ObjectType.CommandBuffer,
                            handle));
            }

            lock (batch)
            {
                ThrowIfCommandBufferCannotRecordDependency(
                    primaryHandle,
                    batch,
                    owner);
                for (int index = 0; index < count; index++)
                {
                    if (handles[index] == 0)
                        continue;
                    batch.RecordDependency(
                        new VulkanResourceLifetimeKey(
                            ObjectType.CommandBuffer,
                            handles[index]));
                }
            }

            for (int index = 0; index < count; index++)
            {
                ulong handle = handles[index];
                if (handle == 0)
                    continue;

                VulkanResourceLifetimeKey key = new(
                    ObjectType.CommandBuffer,
                    handle);
                ulong observedGeneration = observedGenerations[index];
                if (observedGeneration != 0 &&
                    tracker.GetPublishedGeneration(key) == observedGeneration)
                {
                    continue;
                }

                PublishCommandBufferDependencyAfterGenerationRace(
                    primaryHandle,
                    batch,
                    key,
                    observedGeneration,
                    owner);
            }
        }
    }

    private void PublishCommandBufferDependencyAfterGenerationRace(
        ulong commandBufferHandle,
        VulkanCommandBufferTrackingBatch batch,
        VulkanResourceLifetimeKey resourceKey,
        ulong observedGeneration,
        string owner)
    {
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        lock (batch)
        {
            ThrowIfCommandBufferCannotRecordDependency(
                commandBufferHandle,
                batch,
                owner);
            if (!ResourceRuntime.TryValidateCommandBufferRecordingAdmissionNoLock(
                    commandBufferHandle,
                    out string admissionFailure))
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record {resourceKey} for {owner}: {admissionFailure}");
            }
            if (!ResourceRuntime.TryValidateCommandBufferDependencyNoLock(
                    commandBufferHandle,
                    resourceKey,
                    out string dependencyFailure))
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record {resourceKey} for {owner}: {dependencyFailure}");
            }

            VulkanResourceLifetimeRecord resource =
                tracker.GetOrRegisterResourceNoLock(resourceKey, owner);
            if (observedGeneration != 0 &&
                resource.Generation != observedGeneration)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} observed stale {resourceKey} generation {observedGeneration} while generation {resource.Generation} is live for {owner}.");
            }

            if (!tracker.CommandBufferLifetimes.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                tracker.CommandBufferLifetimes.Add(commandBufferHandle, lifetime);
            }

            ResourceRuntime.PublishCommandBufferDependencyNoLock(
                commandBufferHandle,
                lifetime,
                resourceKey);
            lifetime.RefreshTouchedDependencies();
        }
    }

    private void ThrowIfCommandBufferCannotRecordDependency(
        ulong commandBufferHandle,
        VulkanCommandBufferTrackingBatch batch,
        string owner)
    {
        if (!CommandBuffers.TrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? currentBatch) ||
            !ReferenceEquals(batch, currentBatch))
        {
            throw new InvalidOperationException(
                $"Command buffer 0x{commandBufferHandle:X} has no current recording batch for {owner}.");
        }
        if (!batch.IsRecording || batch.QueuedSubmissionCount != 0)
        {
            throw new InvalidOperationException(
                $"Command buffer 0x{commandBufferHandle:X} cannot record dependencies for {owner} while recording={batch.IsRecording}, queued={batch.QueuedSubmissionCount}.");
        }
    }

    internal unsafe Result CreateImageWithLifetime(
        ref ImageCreateInfo createInfo,
        out Image image,
        string owner)
    {
        ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImage." + owner);
        ThrowIfPersistentResourceAllocationDuringCommandRecording(owner);

        image = default;
        fixed (Image* imagePointer = &image)
        {
            Result result = ResourceRuntime.CreateImageTracked(
                Api,
                DeviceContext.Device,
                ref createInfo,
                imagePointer,
                owner);
            if (result == Result.Success && image.Handle != 0)
                RegisterTrackedImageInitialLayouts(image, in createInfo);
            return result;
        }
    }

    internal void DestroyImageWithLifetime(Image image, string owner)
    {
        if (image.Handle == 0)
            return;

        PublishTrackingDependenciesBeforeResourceRetirement(
            new VulkanResourceLifetimeKey(ObjectType.Image, image.Handle));
        ResourceRuntime.DestroyImageImmediateTracked(
            Api,
            DeviceContext.Device,
            image,
            owner);
    }

    internal unsafe void BlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref ImageBlit region,
        Filter filter)
    {
        fixed (ImageBlit* regionPointer = &region)
        {
            BlitImageTracked(
                commandBuffer,
                source,
                sourceLayout,
                destination,
                destinationLayout,
                regionCount,
                regionPointer,
                filter);
        }
    }

    private void ThrowIfPersistentResourceAllocationDuringCommandRecording(string operation)
    {
        if (!ThreadWorkspace.TryGetCurrent(out VulkanCommandThreadContext context) ||
            !ReferenceEquals(context.FrameOpResourcePlannerSwitchingStateOwner, this) ||
            context.FrameOpResourcePlannerSwitchingState?.RecordingScopeActive != true)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Persistent Vulkan resource allocation '{operation}' is forbidden while command recording is active. " +
            "Allocate persistent resources during planning or upload preparation.");
    }
}
