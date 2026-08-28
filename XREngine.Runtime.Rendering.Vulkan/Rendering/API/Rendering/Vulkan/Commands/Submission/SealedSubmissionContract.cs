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
    VulkanTrackedImageSubresource Key,
    VulkanImageAccessState RequiredEntryState,
    ulong SubmittedStateVersion);

/// <summary>One ordered exit state published by a sealed command buffer.</summary>
internal readonly record struct VulkanSealedImageExitState(
    VulkanTrackedImageSubresource Key,
    VulkanImageAccessState State);

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
        VulkanResourceSlotHandle commandBufferSlot,
        ulong lifetimeRecordingGeneration,
        ulong imageRecordingGeneration,
        uint queueFamilyIndex,
        VulkanSealedResourceDependency[] resources,
        VulkanSealedDescriptorDependency[] descriptors,
        VulkanSealedImageDependency[] images,
        VulkanSealedImageExitState[] imageExits)
    {
        CommandBufferHandle = commandBufferHandle;
        CommandBufferSlot = commandBufferSlot;
        LifetimeRecordingGeneration = lifetimeRecordingGeneration;
        ImageRecordingGeneration = imageRecordingGeneration;
        QueueFamilyIndex = queueFamilyIndex;
        Resources = resources;
        Descriptors = descriptors;
        Images = images;
        ImageExits = imageExits;
    }

    internal ulong CommandBufferHandle { get; }
    internal VulkanResourceSlotHandle CommandBufferSlot { get; }
    internal ulong LifetimeRecordingGeneration { get; }
    internal ulong ImageRecordingGeneration { get; }
    internal uint QueueFamilyIndex { get; }
    internal VulkanSealedResourceDependency[] Resources { get; }
    internal VulkanSealedDescriptorDependency[] Descriptors { get; }
    internal VulkanSealedImageDependency[] Images { get; }
    internal VulkanSealedImageExitState[] ImageExits { get; }

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
        mismatchKey = default;
        return EVulkanSealedResourceMatch.Match;
    }

    internal void RefreshCurrentImageVersionsNoLock(
        VulkanCommandSynchronizationState synchronization)
    {
        for (int index = 0; index < Images.Length; ++index)
        {
            VulkanSealedImageDependency dependency = Images[index];
            ulong version = synchronization._trackedImageSubresourceStates.TryGetValue(
                dependency.Key,
                out VulkanImageSubresourceState? state)
                    ? state.SubmittedVersion
                    : 0u;
            Images[index] = dependency with
            {
                SubmittedStateVersion = version,
            };
        }
    }
}
