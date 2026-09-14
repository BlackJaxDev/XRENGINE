using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedSceneResourceRuntime
{
    private bool _reportedFirstSharedPublication;
    private bool _reportedFirstStoragePressure;

    /// <summary>
    /// Gives another output its own globals and receipt while retaining the
    /// exact immutable scene image already owned by this frame generation.
    /// </summary>
    private bool TryBuildSharedPublication(
        VulkanAdvancedSceneResourceSlot slot,
        int frameSlot,
        ulong frameGeneration,
        in VulkanAdvancedScenePublicationState shared,
        AdvancedGpuScenePublicationSnapshot snapshot,
        ReadOnlySpan<BackendReadyCanonicalViewRecord> views,
        in BackendReadyCanonicalFrameRecord frame,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        int diagnosticCount,
        out VulkanAdvancedScenePublicationState state,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        state = default;
        if (!shared.IsValid || shared.FrameSlot != frameSlot ||
            shared.FrameGeneration != frameGeneration ||
            shared.ResourceDescriptorSet.Handle != slot.ResourceDescriptorSet.Handle ||
            (ulong)shared.TextureDescriptorBase + shared.TextureDescriptorCount > slot.NextTextureDescriptor ||
            (ulong)shared.SamplerDescriptorBase + shared.SamplerDescriptorCount > slot.NextSamplerDescriptor ||
            diagnosticCount < 0 || frame.FrameGeneration == 0u)
        {
            failure = EVulkanAdvancedSceneResourceFailure.InvalidPublication;
            reason = "The shared scene payload does not belong to this frame-slot generation or its output metadata is invalid.";
            return false;
        }

        // Strong references preserve lifetime, but mutable texture descriptors
        // can still change after the first output admitted this publication.
        if (!TryValidatePublicationSources(snapshot, out failure, out reason))
            return false;

        VulkanFrameDataArena arena = _resources.FrameDataArena!;
        if (!arena.TryCaptureReservedLaneCursor(
                frameSlot, EVulkanFrameDataLane.AdvancedSceneStorage,
                out ulong rollbackCursor))
        {
            failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = "The shared-publication allocation cursor is unavailable.";
            return false;
        }

        ulong requiredBytes = CalculateFramePublicationStorageBytes(views.Length, passes.Length);
        ulong consumedBytes = AlignUp(Math.Max(slot.StorageBytesConsumed, rollbackCursor), StorageAlignment);
        ulong capacity = _storageCapacityPerFrameSlot[frameSlot];
        if (requiredBytes > capacity || consumedBytes > capacity - requiredBytes)
        {
            failure = IsTransientStoragePressure(slot, consumedBytes, requiredBytes)
                ? EVulkanAdvancedSceneResourceFailure.FrameSlotStillInUse
                : EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            if (failure == EVulkanAdvancedSceneResourceFailure.FrameSlotStillInUse)
                RecordDeferredStorageGrowth(slot, frameSlot, consumedBytes, requiredBytes);
            reason = $"Frame slot {frameSlot} cannot append {requiredBytes} bytes of output globals after {consumedBytes} retained bytes within its {capacity}-byte scene lane.";
            return false;
        }

        ulong plannedEndCursor = checked(consumedBytes + requiredBytes);
        if (!TryUploadViews(frameSlot, views, out VulkanFrameDataSlice viewSlice) ||
            !TryUploadFrameMetadata(frameSlot, in frame, views.Length, passes,
                diagnosticCount, out VulkanFrameDataSlice metadataSlice))
        {
            failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = "The shared-publication output globals could not be uploaded.";
            return RejectSharedPublication(slot, arena, frameSlot, rollbackCursor, ref failure, ref reason);
        }

        if (!arena.TryCaptureReservedLaneCursor(
                frameSlot, EVulkanFrameDataLane.AdvancedSceneStorage,
                out ulong publishedCursor) ||
            AlignUp(publishedCursor, StorageAlignment) != plannedEndCursor)
        {
            failure = EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
            reason = $"The shared-publication allocation plan ended at {plannedEndCursor} bytes, but the arena ended at {publishedCursor} bytes.";
            return RejectSharedPublication(slot, arena, frameSlot, rollbackCursor, ref failure, ref reason);
        }

        // NativeGeneration identifies the scene payload, not this output's
        // globals. Each entry/use still owns its own completion receipt.
        VulkanAdvancedScenePublicationState candidate = shared with
        {
            GlobalDescriptorSet = slot.GlobalDescriptorSets[slot.EntryCount],
            Views = viewSlice,
            FrameMetadata = metadataSlice,
        };
        if (!TryUpdateSharedGlobalDescriptors(in candidate, out reason))
        {
            failure = EVulkanAdvancedSceneResourceFailure.DescriptorUpdateFailed;
            return RejectSharedPublication(slot, arena, frameSlot, rollbackCursor, ref failure, ref reason);
        }

        slot.StorageBytesConsumed = plannedEndCursor;
        state = candidate;
        if (!_reportedFirstSharedPublication)
        {
            _reportedFirstSharedPublication = true;
            Debug.Vulkan(
                "[VulkanAdvancedScene] Shared exact scene payload for another output: nativeGeneration={0}, frameSlot={1}, globalsBytes={2}, totalRetainedBytes={3}, capacityBytes={4}.",
                shared.NativeGeneration, frameSlot, requiredBytes, plannedEndCursor, capacity);
        }
        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    private bool TryUpdateSharedGlobalDescriptors(
        in VulkanAdvancedScenePublicationState state,
        out string reason)
        => TryUpdateGlobalTableDescriptors(
            state.GlobalDescriptorSet, state.FallbackTable,
            state.Draws, state.Instances, state.Geometry, state.Transforms,
            state.Deformations, state.RenderStates, state.EditorIdentities,
            state.Materials, state.ShadingKernels, state.MaterialLayouts,
            state.MaterialConstants, state.MaterialTextureBindings,
            state.Textures, state.Samplers, state.Lights, state.Shadows,
            state.Probes, state.Environments, state.Decals, state.GiResources,
            state.Views, state.FrameMetadata, state.EncodedTextures,
            state.EncodedSamplers, state.HandleLookups, out reason);

    /// <summary>
    /// Occupied storage can grow at the next completed boundary. Demand beyond
    /// the explicit aggregate ceiling remains a visible permanent failure.
    /// </summary>
    private static bool IsTransientStoragePressure(
        VulkanAdvancedSceneResourceSlot slot,
        ulong consumedBytes,
        ulong compactRequiredBytes)
        => compactRequiredBytes <= MaximumStorageCapacityPerFrameSlot &&
           consumedBytes <= MaximumStorageCapacityPerFrameSlot - compactRequiredBytes &&
           !slot.Quarantined &&
           (slot.EntryCount != 0 || slot.ActiveUseCount != 0 || slot.StorageBytesConsumed != 0u);

    /// <summary>
    /// Carries demand across a rejected frame without changing storage beneath
    /// active readers. Reauthoring repeated publication skew must be able to
    /// grow at the next completed boundary instead of retrying the same limit.
    /// </summary>
    private void RecordDeferredStorageGrowth(
        VulkanAdvancedSceneResourceSlot slot,
        int frameSlot,
        ulong consumedBytes,
        ulong requiredBytes)
    {
        ulong demand = checked(consumedBytes + requiredBytes);
        slot.DeferredStorageCapacity = Math.Max(slot.DeferredStorageCapacity, demand);
        if (_reportedFirstStoragePressure)
            return;

        _reportedFirstStoragePressure = true;
        Debug.Vulkan(
            "[VulkanAdvancedScene] Deferred occupied-slot storage pressure: frameSlot={0}, retainedBytes={1}, requiredBytes={2}, nextBoundaryCapacity={3}; retry after native readers retire.",
            frameSlot, consumedBytes, requiredBytes, slot.DeferredStorageCapacity);
    }

    /// <summary>Rolls back only globals; shared resident mirrors stay intact.</summary>
    private static bool RejectSharedPublication(
        VulkanAdvancedSceneResourceSlot slot,
        VulkanFrameDataArena arena,
        int frameSlot,
        ulong rollbackCursor,
        ref EVulkanAdvancedSceneResourceFailure failure,
        ref string reason)
    {
        if (arena.TryRestoreReservedLaneCursor(
                frameSlot, EVulkanFrameDataLane.AdvancedSceneStorage, rollbackCursor))
            return false;

        slot.Quarantined = true;
        slot.TransactionIntegrityFault = true;
        failure = EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
        reason = "The shared-publication transaction could not restore its reserved-lane cursor; the frame slot was quarantined.";
        return false;
    }
}
