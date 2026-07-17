using System.Collections.Generic;

using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _sharedMeshDescriptorAllocationLock = new();
    private readonly Dictionary<
        VkMeshRenderer.DescriptorAllocationKey,
        List<VkMeshRenderer.DescriptorAllocation>> _sharedMeshDescriptorAllocations = [];

    private bool TryAcquireSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        XRMaterial material,
        out VkMeshRenderer.DescriptorAllocation allocation)
    {
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (_sharedMeshDescriptorAllocations.TryGetValue(key, out List<VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    VkMeshRenderer.DescriptorAllocation candidate = candidates[i];
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

    private VkMeshRenderer.DescriptorAllocation PublishSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        VkMeshRenderer.DescriptorAllocation allocation,
        out bool published)
    {
        using VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.DescriptorPublication);
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (!_sharedMeshDescriptorAllocations.TryGetValue(key, out List<VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                candidates = [];
                _sharedMeshDescriptorAllocations.Add(key, candidates);
            }
            else
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    VkMeshRenderer.DescriptorAllocation candidate = candidates[i];
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

    private bool ReleaseSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        VkMeshRenderer.DescriptorAllocation allocation)
    {
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (allocation.SharedReferenceCount > 0)
                allocation.SharedReferenceCount--;
            if (allocation.SharedReferenceCount != 0)
                return false;

            if (_sharedMeshDescriptorAllocations.TryGetValue(key, out List<VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                candidates.Remove(allocation);
                if (candidates.Count == 0)
                    _sharedMeshDescriptorAllocations.Remove(key);
            }

            return true;
        }
    }
}
