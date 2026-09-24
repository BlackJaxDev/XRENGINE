using Silk.NET.Vulkan;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    /// <summary>
    /// Retires descriptor cache owners which still publish an exact retiring
    /// native resource generation. Call only at a normal frame preparation boundary,
    /// before command recording starts for that frame.
    /// </summary>
    internal int DrainPendingSupersededDescriptorOwners()
    {
        Descriptors.DrainReleasedMaterialDescriptorClosures();
        int releasedOwnerCount =
            Descriptors.DrainPendingSupersededComputePoolRetirements();
        releasedOwnerCount +=
            DescriptorLifetime.DrainPendingMeshDescriptorPoolSlabRetirements();
        releasedOwnerCount +=
            DescriptorLifetime.DrainPendingMeshDescriptorSetRetirements();
        while (Lifetime.TryDequeueSupersededResourceDescriptorOwner(
                   out VulkanSupersededResourceDescriptorOwner pending))
        {
            try
            {
                VulkanDescriptorSetGenerationReference[] affected =
                    SnapshotDescriptorOwnersForSupersededResource(pending);
                if (affected.Length == 0)
                    continue;

                releasedOwnerCount +=
                    Descriptors.RetireSupersededComputeDescriptorPools(affected);

                VkObject<XRMaterial>[] materials = BackendObjects.Snapshot<XRMaterial>();
                for (int index = 0; index < materials.Length; index++)
                {
                    if (materials[index] is VkMaterial material)
                        releasedOwnerCount +=
                            material.ReleaseSupersededDescriptorProgramStates(affected, pending);
                }

                VkObject<XRMeshRenderer.BaseVersion>[] meshes =
                    BackendObjects.Snapshot<XRMeshRenderer.BaseVersion>();
                for (int index = 0; index < meshes.Length; index++)
                {
                    if (meshes[index] is VkMeshRenderer mesh)
                        releasedOwnerCount +=
                            mesh.ReleaseSupersededDescriptorAllocations(affected, pending);
                }
            }
            catch
            {
                // Keep the exact generation queued for a later normal boundary;
                // cache eviction must never turn a transient failure into a leak.
                Lifetime.EnqueueSupersededResourceDescriptorOwner(
                    pending.ResourceKey,
                    pending.Generation);
                throw;
            }
        }

        return releasedOwnerCount;
    }

    internal bool IsDescriptorSetGenerationCurrent(
        in VulkanDescriptorSetGenerationReference descriptorSet)
    {
        if (descriptorSet.Set.Handle == 0 || descriptorSet.Generation == 0)
            return false;

        lock (Lifetime.Tracker.SyncRoot)
            return Lifetime.Tracker.DescriptorSetLifetimes.TryGetValue(
                       descriptorSet.Set.Handle,
                       out VulkanDescriptorSetLifetimeRecord? state) &&
                   state.Generation == descriptorSet.Generation;
    }

    private VulkanDescriptorSetGenerationReference[]
        SnapshotDescriptorOwnersForSupersededResource(
            in VulkanSupersededResourceDescriptorOwner pending)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            List<VulkanDescriptorSetGenerationReference>? result = null;
            foreach ((ulong descriptorSetHandle, VulkanDescriptorSetLifetimeRecord state)
                     in Lifetime.Tracker.DescriptorSetLifetimes)
            {
                if (!state.PinnedReferences.TryGetValue(
                        pending.ResourceKey,
                        out ulong pinnedGeneration) ||
                    pinnedGeneration != pending.Generation ||
                    state.Generation == 0)
                {
                    continue;
                }

                (result ??= []).Add(new VulkanDescriptorSetGenerationReference(
                    new Silk.NET.Vulkan.DescriptorSet(descriptorSetHandle),
                    state.Generation));
            }

            return result is null ? [] : [.. result];
        }
    }
}
