using Silk.NET.Vulkan;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private long _supersededResourceGenerationsDrained;
    private long _supersededDescriptorOwnersReleased;
    private long _supersededDescriptorOwnerDrainDeferrals;

    /// <summary>Retired resource generations that <see cref="DrainPendingSupersededDescriptorOwners"/> took from the queue, since launch.</summary>
    internal long SupersededResourceGenerationsDrained => Interlocked.Read(ref _supersededResourceGenerationsDrained);

    /// <summary>Descriptor cache owners (material program states, mesh allocations, compute pools) that the drain released, since launch.</summary>
    internal long SupersededDescriptorOwnersReleased => Interlocked.Read(ref _supersededDescriptorOwnersReleased);

    /// <summary>OpenXR boundaries that did not run the drain because a desktop frame was still recording, since launch.</summary>
    internal long SupersededDescriptorOwnerDrainDeferrals => Interlocked.Read(ref _supersededDescriptorOwnerDrainDeferrals);

    /// <summary>Retired resource generations that wait for the next drain.</summary>
    internal int PendingSupersededResourceGenerationCount => Lifetime.PendingSupersededResourceDescriptorOwnerCount;

    /// <summary>Counts one OpenXR boundary that left the drain for later because a desktop frame was recording.</summary>
    internal void RecordSupersededDescriptorOwnerDrainDeferral()
        => Interlocked.Increment(ref _supersededDescriptorOwnerDrainDeferrals);

    /// <summary>
    /// Retires descriptor cache owners which still publish an exact retiring
    /// native resource generation. Call only at a frame preparation boundary where
    /// no command recording reads the material or mesh descriptor caches: the desktop
    /// frame-slot boundary, or the OpenXR view boundary while no desktop frame is active.
    /// </summary>
    internal int DrainPendingSupersededDescriptorOwners()
    {
        DrainDestroyedDirectComputeOutputs();
        Descriptors.DrainReleasedMaterialDescriptorClosures();
        int releasedOwnerCount =
            Descriptors.DrainPendingSupersededComputePoolRetirements();
        releasedOwnerCount +=
            DescriptorLifetime.DrainPendingMeshDescriptorPoolSlabRetirements();
        releasedOwnerCount +=
            DescriptorLifetime.DrainPendingMeshDescriptorSetRetirements();
        // Take every waiting generation at once. A resize retires hundreds of render target generations, and
        // the owner scans below then run once for all of them instead of once for each generation.
        VulkanSupersededDescriptorOwnerBatch? batch = null;
        int dequeuedCount = 0;
        while (Lifetime.TryDequeueSupersededResourceDescriptorOwner(
                   out VulkanSupersededResourceDescriptorOwner pending))
        {
            (batch ??= new VulkanSupersededDescriptorOwnerBatch()).Add(pending);
            dequeuedCount++;
        }

        if (batch is not null)
        {
            try
            {
                Interlocked.Add(ref _supersededResourceGenerationsDrained, dequeuedCount);
                SnapshotDescriptorOwnersForSupersededResources(batch);
                if (!batch.AffectedSets.IsEmpty)
                    releasedOwnerCount += ReleaseSupersededDescriptorOwners(batch);
            }
            catch
            {
                // Keep the exact generations queued for a later normal boundary;
                // cache eviction must never turn a transient failure into a leak.
                foreach (VulkanSupersededResourceDescriptorOwner pending in batch.Pending)
                    Lifetime.EnqueueSupersededResourceDescriptorOwner(
                        pending.ResourceKey,
                        pending.Generation);
                throw;
            }
        }

        Interlocked.Add(ref _supersededDescriptorOwnersReleased, releasedOwnerCount);
        return releasedOwnerCount;
    }

    /// <summary>
    /// Releases the compute descriptor pools, material program states, and mesh descriptor allocations whose
    /// descriptor sets pin a retired generation of the batch. Each owner collection is scanned once.
    /// </summary>
    private int ReleaseSupersededDescriptorOwners(VulkanSupersededDescriptorOwnerBatch batch)
    {
        int releasedOwnerCount = Descriptors.RetireSupersededComputeDescriptorPools(batch.AffectedSets);

        VkObject<XRMaterial>[] materials = BackendObjects.Snapshot<XRMaterial>();
        for (int index = 0; index < materials.Length; index++)
        {
            if (materials[index] is VkMaterial material)
                releasedOwnerCount += material.ReleaseSupersededDescriptorProgramStates(batch);
        }

        VkObject<XRMeshRenderer.BaseVersion>[] meshes =
            BackendObjects.Snapshot<XRMeshRenderer.BaseVersion>();
        for (int index = 0; index < meshes.Length; index++)
        {
            if (meshes[index] is VkMeshRenderer mesh)
                releasedOwnerCount += mesh.ReleaseSupersededDescriptorAllocations(batch);
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

    /// <summary>
    /// Finds, in one pass over the tracked descriptor sets, each set that pins a retired generation of the batch,
    /// and stores those sets in the batch.
    /// </summary>
    private void SnapshotDescriptorOwnersForSupersededResources(VulkanSupersededDescriptorOwnerBatch batch)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            List<VulkanDescriptorSetGenerationReference>? result = null;
            foreach ((ulong descriptorSetHandle, VulkanDescriptorSetLifetimeRecord state)
                     in Lifetime.Tracker.DescriptorSetLifetimes)
            {
                if (state.Generation == 0 || !batch.PinsPendingGeneration(state))
                    continue;

                (result ??= []).Add(new VulkanDescriptorSetGenerationReference(
                    new Silk.NET.Vulkan.DescriptorSet(descriptorSetHandle),
                    state.Generation));
            }

            batch.SetAffectedSets(result);
        }
    }
}
