namespace XREngine.Rendering.Vulkan;

/// <summary>One flat native resource generation required by a sealed submission.</summary>
internal readonly record struct VulkanSealedResourceDependency(
    VulkanResourceSlotHandle Slot,
    VulkanResourceLifetimeKey Key,
    ulong Generation)
{
    internal VulkanSealedResourceDependency(
        VulkanResourceSlotHandle slot,
        VulkanResourceLifetimeKey key)
        : this(slot, key, slot.Generation)
    {
    }
}

/// <summary>One immutable descriptor publication required by a sealed submission.</summary>
internal readonly record struct VulkanSealedDescriptorDependency(
    VulkanResourceSlotHandle DescriptorSetSlot,
    VulkanResourceLifetimeKey Key,
    ulong ResourceClosureGeneration,
    ulong ImagePayloadGeneration);

/// <summary>One generation-tagged image entry state required by a sealed submission.</summary>
internal readonly record struct VulkanSealedImageDependency(
    VulkanStableImageSubresourceSlotHandle Slot,
    VulkanImageAccessState RequiredEntryState,
    ulong SubmittedStateVersion);

/// <summary>One ordered exit state published by a sealed command buffer.</summary>
internal readonly record struct VulkanSealedImageExitState(
    VulkanStableImageSubresourceSlotHandle Slot,
    VulkanImageAccessState State);

/// <summary>Exact secondary recording embedded by a reusable primary.</summary>
internal readonly record struct VulkanSealedNestedCommandDependency(
    VulkanRecordedCommandArtifactReference Artifact,
    VulkanResourceSlotHandle CommandBufferSlot,
    VulkanCommandBufferLifetimeRecord Lifetime);

internal enum EVulkanSealedResourceMatch
{
    Match,
    CommandBuffer,
    DescriptorPublication,
    Resource,
}

/// <summary>
/// Prevalidated, immutable dependency manifest for the ordinary reusable
/// graphics submission path. Complex ordered overlays and cross-queue
/// ownership transfers deliberately remain on the full validator.
/// </summary>
internal sealed class SealedSubmissionContract
{
    internal SealedSubmissionContract(
        ulong commandBufferHandle,
        VulkanStableCommandSlotHandle stableCommandIdentity,
        VulkanResourceSlotHandle commandBufferSlot,
        ulong lifetimeRecordingGeneration,
        ulong imageRecordingGeneration,
        uint queueFamilyIndex,
        VulkanSealedResourceDependency[] resources,
        VulkanSealedDescriptorDependency[] descriptors,
        VulkanSealedImageDependency[] images,
        VulkanSealedImageExitState[] imageExits,
        VulkanQueueOwnershipTransferRequirement[] queueOwnershipTransfers,
        VulkanRecordedRenderTargetSnapshot renderTarget,
        VulkanSealedResourceDependency[] renderTargetResources,
        VulkanSealedNestedCommandDependency[] nestedCommands)
    {
        CommandBufferHandle = commandBufferHandle;
        StableCommandIdentity = stableCommandIdentity;
        CommandBufferSlot = commandBufferSlot;
        LifetimeRecordingGeneration = lifetimeRecordingGeneration;
        ImageRecordingGeneration = imageRecordingGeneration;
        QueueFamilyIndex = queueFamilyIndex;
        Resources = resources;
        Descriptors = descriptors;
        Images = images;
        ImageExits = imageExits;
        QueueOwnershipTransfers = queueOwnershipTransfers;
        RenderTarget = renderTarget;
        RenderTargetResources = renderTargetResources;
        NestedCommands = nestedCommands;
    }

    internal ulong CommandBufferHandle { get; }
    internal VulkanStableCommandSlotHandle StableCommandIdentity { get; }
    internal VulkanResourceSlotHandle CommandBufferSlot { get; }
    internal ulong LifetimeRecordingGeneration { get; }
    internal ulong ImageRecordingGeneration { get; }
    internal uint QueueFamilyIndex { get; }
    internal VulkanSealedResourceDependency[] Resources { get; }
    internal VulkanSealedDescriptorDependency[] Descriptors { get; }
    internal VulkanSealedImageDependency[] Images { get; }
    internal VulkanSealedImageExitState[] ImageExits { get; }
    /// <summary>
    /// Immutable ownership requirements captured at record time. Their live
    /// release/acquire and semaphore dependencies are intentionally checked
    /// during submission.
    /// </summary>
    internal VulkanQueueOwnershipTransferRequirement[] QueueOwnershipTransfers { get; }
    internal VulkanRecordedRenderTargetSnapshot RenderTarget { get; }
    internal VulkanSealedResourceDependency[] RenderTargetResources { get; }
    internal VulkanSealedNestedCommandDependency[] NestedCommands { get; }

    internal EVulkanSealedResourceMatch MatchResourceVectorNoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanCommandBufferLifetimeRecord lifetime,
        out VulkanResourceLifetimeKey mismatchKey)
    {
        mismatchKey = new VulkanResourceLifetimeKey(
            Silk.NET.Vulkan.ObjectType.CommandBuffer,
            CommandBufferHandle);
        if (lifetime.RecordingGeneration != LifetimeRecordingGeneration ||
            !tracker.TryResolvePublishedResourceSlotNoLock(
                CommandBufferSlot,
                out VulkanResourceLifetimeRecord commandBuffer) ||
            commandBuffer.Key != mismatchKey ||
            (commandBuffer.State &
             (EVulkanResourceLifetimeState.PendingRetirement |
              EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            return EVulkanSealedResourceMatch.CommandBuffer;
        }

        for (int index = 0; index < Descriptors.Length; ++index)
        {
            VulkanSealedDescriptorDependency descriptor = Descriptors[index];
            mismatchKey = descriptor.Key;
            if (!tracker.TryGetPublishedDescriptorSnapshotNoLock(
                    descriptor.DescriptorSetSlot,
                    out VulkanPublishedDescriptorSetSnapshot snapshot) ||
                !snapshot.IsNativePublicationKnown ||
                snapshot.ResourceClosureGeneration !=
                    descriptor.ResourceClosureGeneration ||
                snapshot.ImagePayloadGeneration != descriptor.ImagePayloadGeneration)
            {
                return EVulkanSealedResourceMatch.DescriptorPublication;
            }
        }

        for (int index = 0; index < Resources.Length; ++index)
        {
            VulkanSealedResourceDependency dependency = Resources[index];
            mismatchKey = dependency.Key;
            if (!tracker.TryResolvePublishedResourceSlotNoLock(
                    dependency.Slot,
                    out VulkanResourceLifetimeRecord resource) ||
                resource.Key != dependency.Key ||
                resource.Generation != dependency.Generation)
            {
                return EVulkanSealedResourceMatch.Resource;
            }
        }

        if (!MatchesRenderTargetNoLock(tracker) ||
            !MatchesNestedCommandsNoLock(tracker))
        {
            return EVulkanSealedResourceMatch.Resource;
        }
        mismatchKey = default;
        return EVulkanSealedResourceMatch.Match;
    }

    private bool MatchesRenderTargetNoLock(VulkanResourceLifetimeTracker tracker)
    {
        if (!RenderTarget.IsComplete)
            return true;

        for (int index = 0; index < RenderTargetResources.Length; ++index)
        {
            VulkanSealedResourceDependency dependency = RenderTargetResources[index];
            if (!tracker.TryResolvePublishedResourceSlotNoLock(
                    dependency.Slot,
                    out VulkanResourceLifetimeRecord resource) ||
                resource.Key != dependency.Key ||
                resource.Generation != dependency.Generation)
            {
                return false;
            }
        }

        return true;
    }

    private bool MatchesNestedCommandsNoLock(VulkanResourceLifetimeTracker tracker)
    {
        for (int index = 0; index < NestedCommands.Length; ++index)
        {
            VulkanSealedNestedCommandDependency dependency =
                NestedCommands[index];
            VulkanRecordedCommandArtifactReference artifact = dependency.Artifact;
            ulong handle = unchecked((ulong)artifact.NativeBuffer.Handle);
            if (!artifact.IsExecutable || handle == 0UL ||
                dependency.Lifetime.Level != artifact.Level ||
                dependency.Lifetime.RecordingGeneration != artifact.RecordingGeneration ||
                !tracker.TryResolvePublishedResourceSlotNoLock(
                    dependency.CommandBufferSlot,
                    out VulkanResourceLifetimeRecord commandBuffer) ||
                commandBuffer.Key.Type != Silk.NET.Vulkan.ObjectType.CommandBuffer ||
                commandBuffer.Key.Handle != handle ||
                commandBuffer.Generation != dependency.CommandBufferSlot.Generation)
            {
                return false;
            }
        }

        return true;
    }

    internal void RefreshCurrentImageVersionsNoLock(
        VulkanCommandSynchronizationState synchronization)
    {
        for (int index = 0; index < Images.Length; ++index)
        {
            VulkanSealedImageDependency dependency = Images[index];
            ulong version = synchronization.TryGetStableImageSubresourceStateNoLock(
                dependency.Slot,
                out VulkanImageSubresourceState? state)
                    ? state!.SubmittedVersion
                    : 0UL;
            Images[index] = dependency with
            {
                SubmittedStateVersion = version,
            };
        }
    }
}
