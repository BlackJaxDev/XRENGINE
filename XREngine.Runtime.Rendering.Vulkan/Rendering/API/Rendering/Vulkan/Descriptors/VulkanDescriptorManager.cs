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
    internal readonly object _descriptorSetLayoutCacheLock = new();
    internal readonly Dictionary<ulong, List<CachedDescriptorSetLayout>> _descriptorSetLayoutsByHash = new();
    internal readonly Dictionary<ulong, CachedDescriptorSetLayout> _descriptorSetLayoutsByHandle = new();
    internal readonly object _descriptorUpdateTemplateCacheLock = new();
    internal readonly Dictionary<ulong, List<CachedDescriptorUpdateTemplate>> _descriptorUpdateTemplateCache = new();
    private readonly object _sharedMeshDescriptorAllocationLock = new();
    private readonly Dictionary<
        VkMeshRenderer.DescriptorAllocationKey,
        List<VkMeshRenderer.DescriptorAllocation>> _sharedMeshDescriptorAllocations = [];
    private long _descriptorSetContentUpdateGeneration;
    private int _descriptorUpdateInvalidationDiagnosticCount;
    private int _meshOwnershipDiagnosticCount;
    private int _frameSlotCount = 2;

    internal Sampler[] CanonicalImmutableSamplers { get; } = new Sampler[5];
    internal ConcurrentDictionary<ulong, string> LiveDescriptorSetLayoutHandles { get; } = new();
    internal object MeshDescriptorPoolSlabLock { get; } = new();
    internal object SamplerLifetimeLock { get; } = new();
    internal HashSet<ulong> LiveSamplerHandles { get; } = [];
    internal Dictionary<ulong, SamplerCreateInfo> DescriptorHeapSamplerCreateInfos { get; } = [];
    internal ConcurrentDictionary<ulong, BufferViewCreateInfo> DescriptorHeapBufferViewCreateInfos { get; } = new();
    internal Dictionary<
        MeshDescriptorPoolSlabKey,
        List<MeshDescriptorPoolSlab>> MeshDescriptorPoolSlabs { get; } = [];
    internal VulkanBindlessMaterialTextureTableState BindlessMaterialTextures { get; } = new();
    internal VulkanComputeDescriptorCacheState Compute { get; } = new();
    internal VulkanDescriptorHeapState Heap { get; } = new();
    internal DescriptorSet[]? RootSets;
    internal DescriptorPool RootPool;
    internal DescriptorSetLayout RootSetLayout;

    internal int FrameSlotCount => Volatile.Read(ref _frameSlotCount);

    internal bool EnsureFrameSlotCountFloor(int frameSlotCount)
    {
        if (frameSlotCount <= 0)
            return false;

        while (true)
        {
            int current = Volatile.Read(ref _frameSlotCount);
            if (current >= frameSlotCount)
                return false;

            if (Interlocked.CompareExchange(ref _frameSlotCount, frameSlotCount, current) == current)
                return true;
        }
    }
    internal long SnapshotDescriptorSetContentUpdateGeneration()
        => Volatile.Read(ref _descriptorSetContentUpdateGeneration);

    internal void RegisterLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return;

        lock (SamplerLifetimeLock)
            LiveSamplerHandles.Add(sampler.Handle);
    }

    internal void RegisterLiveSampler(Sampler sampler, in SamplerCreateInfo createInfo)
    {
        if (sampler.Handle == 0)
            return;

        lock (SamplerLifetimeLock)
        {
            LiveSamplerHandles.Add(sampler.Handle);
            DescriptorHeapSamplerCreateInfos[sampler.Handle] = createInfo with { PNext = null };
        }
    }

    internal void UnregisterLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return;

        lock (SamplerLifetimeLock)
        {
            LiveSamplerHandles.Remove(sampler.Handle);
            DescriptorHeapSamplerCreateInfos.Remove(sampler.Handle);
        }
    }

    internal bool IsLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return false;

        lock (SamplerLifetimeLock)
            return LiveSamplerHandles.Contains(sampler.Handle);
    }

    internal bool TryGetSamplerCreateInfo(Sampler sampler, out SamplerCreateInfo createInfo)
    {
        if (sampler.Handle != 0)
        {
            lock (SamplerLifetimeLock)
            {
                if (DescriptorHeapSamplerCreateInfos.TryGetValue(sampler.Handle, out createInfo))
                    return true;
            }
        }

        createInfo = default;
        return false;
    }

    internal ulong[] TakeLiveSamplerHandles()
    {
        lock (SamplerLifetimeLock)
        {
            if (LiveSamplerHandles.Count == 0)
                return [];

            ulong[] handles = [.. LiveSamplerHandles];
            LiveSamplerHandles.Clear();
            DescriptorHeapSamplerCreateInfos.Clear();
            return handles;
        }
    }

    internal bool HaveDescriptorSetContentsUpdatedSince(long generation)
        => Volatile.Read(ref _descriptorSetContentUpdateGeneration) != generation;

    internal void RecordDescriptorSetContentUpdate()
        => Interlocked.Increment(ref _descriptorSetContentUpdateGeneration);

    internal int RecordDescriptorUpdateInvalidationDiagnostic()
        => Interlocked.Increment(ref _descriptorUpdateInvalidationDiagnosticCount);

    internal int RecordMeshOwnershipDiagnostic()
        => Interlocked.Increment(ref _meshOwnershipDiagnosticCount);

    internal bool TryAcquireSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        XRMaterial material,
        out VkMeshRenderer.DescriptorAllocation allocation)
    {
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VkMeshRenderer.DescriptorAllocation>? candidates))
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

    internal VkMeshRenderer.DescriptorAllocation PublishSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        VkMeshRenderer.DescriptorAllocation allocation,
        out bool published)
    {
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (!_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VkMeshRenderer.DescriptorAllocation>? candidates))
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

    internal bool ReleaseSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        VkMeshRenderer.DescriptorAllocation allocation)
    {
        lock (_sharedMeshDescriptorAllocationLock)
        {
            if (allocation.SharedReferenceCount > 0)
                allocation.SharedReferenceCount--;
            if (allocation.SharedReferenceCount != 0)
                return false;

            if (_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                candidates.Remove(allocation);
                if (candidates.Count == 0)
                    _sharedMeshDescriptorAllocations.Remove(key);
            }

            return true;
        }
    }
}
