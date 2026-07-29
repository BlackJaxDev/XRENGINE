using System.Collections.Concurrent;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns descriptor caches and allocation bookkeeping for one Vulkan logical-device lifetime.
/// Native creation and retirement remain renderer operations so they continue to use the
/// renderer's lifetime ledger and device-loss policy.
/// </summary>
internal sealed class VulkanDescriptorManager
{
    private readonly object _sharedMeshDescriptorAllocationLock = new();
    private readonly Dictionary<
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey,
        List<VulkanRenderer.VkMeshRenderer.DescriptorAllocation>> _sharedMeshDescriptorAllocations = [];
    private long _descriptorSetContentUpdateGeneration;
    private int _descriptorUpdateInvalidationDiagnosticCount;
    private int _meshOwnershipDiagnosticCount;

    internal Sampler[] CanonicalImmutableSamplers { get; } = new Sampler[5];
    internal ConcurrentDictionary<ulong, string> LiveDescriptorSetLayoutHandles { get; } = new();
    internal object MeshDescriptorPoolSlabLock { get; } = new();
    internal Dictionary<
        VulkanRenderer.MeshDescriptorPoolSlabKey,
        List<VulkanRenderer.MeshDescriptorPoolSlab>> MeshDescriptorPoolSlabs { get; } = [];

    internal long SnapshotDescriptorSetContentUpdateGeneration()
        => Volatile.Read(ref _descriptorSetContentUpdateGeneration);

    internal bool HaveDescriptorSetContentsUpdatedSince(long generation)
        => Volatile.Read(ref _descriptorSetContentUpdateGeneration) != generation;

    internal void RecordDescriptorSetContentUpdate()
        => Interlocked.Increment(ref _descriptorSetContentUpdateGeneration);

    internal int RecordDescriptorUpdateInvalidationDiagnostic()
        => Interlocked.Increment(ref _descriptorUpdateInvalidationDiagnosticCount);

    internal int RecordMeshOwnershipDiagnostic()
        => Interlocked.Increment(ref _meshOwnershipDiagnosticCount);

    internal bool TryAcquireSharedMeshDescriptorAllocation(
        in VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey key,
        XRMaterial material,
        out VulkanRenderer.VkMeshRenderer.DescriptorAllocation allocation)
    {
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VulkanRenderer.VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    VulkanRenderer.VkMeshRenderer.DescriptorAllocation candidate = candidates[i];
                    if (candidate.UsesSharedMaterialTier &&
                        !ReferenceEquals(candidate.Material, material))
                    {
                        continue;
                    }

                    candidate.SharedReferenceCount++;
                    allocation = candidate;
                    return true;
                }
            }
        }

        allocation = null!;
        return false;
    }

    internal VulkanRenderer.VkMeshRenderer.DescriptorAllocation PublishSharedMeshDescriptorAllocation(
        in VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey key,
        VulkanRenderer.VkMeshRenderer.DescriptorAllocation allocation,
        out bool published)
    {
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (!_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VulkanRenderer.VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                candidates = [];
                _sharedMeshDescriptorAllocations.Add(key, candidates);
            }
            else
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    VulkanRenderer.VkMeshRenderer.DescriptorAllocation candidate = candidates[i];
                    if (candidate.UsesSharedMaterialTier &&
                        !ReferenceEquals(candidate.Material, allocation.Material))
                    {
                        continue;
                    }

                    candidate.SharedReferenceCount++;
                    published = false;
                    return candidate;
                }
            }

            allocation.SharedReferenceCount = 1;
            candidates.Add(allocation);
            published = true;
            return allocation;
        }
    }

    internal bool ReleaseSharedMeshDescriptorAllocation(
        in VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey key,
        VulkanRenderer.VkMeshRenderer.DescriptorAllocation allocation)
    {
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (allocation.SharedReferenceCount > 0)
                allocation.SharedReferenceCount--;
            if (allocation.SharedReferenceCount != 0)
                return false;

            if (_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VulkanRenderer.VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                candidates.Remove(allocation);
                if (candidates.Count == 0)
                    _sharedMeshDescriptorAllocations.Remove(key);
            }

            return true;
        }
    }
}
