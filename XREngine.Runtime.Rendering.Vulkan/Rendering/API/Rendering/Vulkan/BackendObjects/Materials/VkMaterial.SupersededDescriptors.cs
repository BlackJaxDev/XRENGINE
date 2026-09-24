using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMaterial
{
    /// <summary>
    /// Detaches only program states whose material sets still pin an exact retired
    /// native resource generation. Their pools follow normal deferred retirement,
    /// while a later bind recreates current descriptors for every frame slot.
    /// </summary>
    internal int ReleaseSupersededDescriptorProgramStates(
        ReadOnlySpan<VulkanDescriptorSetGenerationReference> affectedSets,
        in VulkanSupersededResourceDescriptorOwner pending)
    {
        if (affectedSets.IsEmpty)
            return 0;

        lock (_stateSync)
        {
            List<uint>? keysToRelease = null;
            foreach ((uint key, ProgramDescriptorState state) in _programStates)
            {
                if (ProgramStatePinsSupersededResource(state, affectedSets, pending))
                    (keysToRelease ??= []).Add(key);
            }
            if (keysToRelease is null)
                return 0;

            int releasedCount = 0;
            for (int index = 0; index < keysToRelease.Count; index++)
            {
                uint key = keysToRelease[index];
                if (!_programStates.TryGetValue(key, out ProgramDescriptorState? state))
                    continue;
                DestroyProgramState(state);
                _programStates.Remove(key);
                releasedCount++;
            }

            if (releasedCount != 0)
                _materialDirty = true;
            return releasedCount;
        }
    }

    private bool ProgramStatePinsSupersededResource(
        ProgramDescriptorState programState,
        ReadOnlySpan<VulkanDescriptorSetGenerationReference> affectedSets,
        in VulkanSupersededResourceDescriptorOwner pending)
    {
        VulkanResourceLifetimeTracker tracker = BackendContext.Resources.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            for (int frame = 0; frame < programState.DescriptorSets.Length; frame++)
            {
                DescriptorSet[] sets = programState.DescriptorSets[frame];
                if ((uint)sets.Length <= VulkanDescriptorManager.MaterialSetIndex ||
                    frame >= programState.MaterialSetLifetimeSlots.Length)
                    continue;

                ulong handle = sets[VulkanDescriptorManager.MaterialSetIndex].Handle;
                VulkanResourceSlotHandle slot = programState.MaterialSetLifetimeSlots[frame];
                if (handle == 0 || !slot.IsValid ||
                    !tracker.TryResolveResourceSlotNoLock(
                        slot,
                        out VulkanResourceLifetimeRecord resource) ||
                    resource.Key != new VulkanResourceLifetimeKey(ObjectType.DescriptorSet, handle) ||
                    !tracker.DescriptorSetLifetimes.TryGetValue(
                        handle,
                        out VulkanDescriptorSetLifetimeRecord? descriptorState) ||
                    descriptorState.Pool.Handle != programState.DescriptorPool.Handle ||
                    !descriptorState.PinnedReferences.TryGetValue(
                        pending.ResourceKey,
                        out ulong pinnedGeneration) ||
                    pinnedGeneration != pending.Generation)
                    continue;

                for (int affectedIndex = 0; affectedIndex < affectedSets.Length; affectedIndex++)
                {
                    if (affectedSets[affectedIndex].Set.Handle == handle)
                        return true;
                }
            }
        }

        return false;
    }
}
