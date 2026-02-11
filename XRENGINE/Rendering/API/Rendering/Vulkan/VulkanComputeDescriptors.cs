using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _computeDescriptorCacheLock = new();
    private ComputeDescriptorImageCache[]? _computeDescriptorCaches;

    private sealed class ComputeDescriptorImageCache
    {
        public Dictionary<ComputeDescriptorCacheKey, DescriptorSet[]> CachedSets { get; } = new();
        public Dictionary<ulong, List<ComputeDescriptorPoolBlock>> PoolsBySchema { get; } = new();
    }

    private sealed class ComputeDescriptorPoolBlock
    {
        public DescriptorPool Pool;
        public uint MaxAllocations;
        public uint AllocatedAllocations;
    }

    private readonly record struct ComputeDescriptorCacheKey(ulong SchemaKey, ulong BindingKey);

    private void InitializeComputeDescriptorCaches(int imageCount)
    {
        lock (_computeDescriptorCacheLock)
        {
            DestroyComputeDescriptorCaches_NoLock();

            _computeDescriptorCaches = new ComputeDescriptorImageCache[imageCount];
            for (int i = 0; i < imageCount; i++)
                _computeDescriptorCaches[i] = new ComputeDescriptorImageCache();
        }
    }

    private void DestroyComputeDescriptorCaches()
    {
        lock (_computeDescriptorCacheLock)
            DestroyComputeDescriptorCaches_NoLock();
    }

    private void DestroyComputeDescriptorCaches_NoLock()
    {
        if (_computeDescriptorCaches is null)
            return;

        HashSet<ulong> destroyedPools = [];
        foreach (ComputeDescriptorImageCache cache in _computeDescriptorCaches)
        {
            foreach (List<ComputeDescriptorPoolBlock> blocks in cache.PoolsBySchema.Values)
            {
                foreach (ComputeDescriptorPoolBlock block in blocks)
                {
                    if (block.Pool.Handle == 0 || !destroyedPools.Add(block.Pool.Handle))
                        continue;

                    Api!.DestroyDescriptorPool(device, block.Pool, null);
                }
            }
        }

        _computeDescriptorCaches = null;
    }

    internal bool TryGetOrCreateComputeDescriptorSets(
        uint imageIndex,
        ulong schemaKey,
        ulong bindingKey,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] perAllocationPoolSizes,
        out DescriptorSet[] descriptorSets,
        out bool isNewAllocation)
    {
        descriptorSets = Array.Empty<DescriptorSet>();
        isNewAllocation = false;

        if (layouts is null || layouts.Length == 0)
            return false;

        if (perAllocationPoolSizes is null || perAllocationPoolSizes.Length == 0)
            return false;

        lock (_computeDescriptorCacheLock)
        {
            if (_computeDescriptorCaches is null || imageIndex >= _computeDescriptorCaches.Length)
                return false;

            ComputeDescriptorImageCache cache = _computeDescriptorCaches[imageIndex];
            ComputeDescriptorCacheKey key = new(schemaKey, bindingKey);
            if (cache.CachedSets.TryGetValue(key, out descriptorSets))
                return true;

            if (!TryAllocateDescriptorSetBatch(cache, schemaKey, layouts, perAllocationPoolSizes, out descriptorSets))
                return false;

            cache.CachedSets[key] = descriptorSets;
            isNewAllocation = true;
            return true;
        }
    }

    private bool TryAllocateDescriptorSetBatch(
        ComputeDescriptorImageCache cache,
        ulong schemaKey,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] perAllocationPoolSizes,
        out DescriptorSet[] descriptorSets)
    {
        descriptorSets = Array.Empty<DescriptorSet>();
        const uint baseAllocationCapacity = 32;

        if (!cache.PoolsBySchema.TryGetValue(schemaKey, out List<ComputeDescriptorPoolBlock>? blocks))
        {
            blocks = [];
            cache.PoolsBySchema[schemaKey] = blocks;
        }

        foreach (ComputeDescriptorPoolBlock block in blocks)
        {
            if (block.AllocatedAllocations >= block.MaxAllocations)
                continue;

            Result allocResult = TryAllocateDescriptorSetsFromPool(block.Pool, layouts, out descriptorSets);
            if (allocResult == Result.Success)
            {
                block.AllocatedAllocations++;
                return true;
            }

            if (allocResult is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)
            {
                block.AllocatedAllocations = block.MaxAllocations;
                continue;
            }

            return false;
        }

        // Exhausted or fragmented: grow by creating a new pool block and retry.
        uint targetAllocations = blocks.Count switch
        {
            <= 0 => baseAllocationCapacity,
            _ => baseAllocationCapacity + (uint)(blocks.Count * 16)
        };

        if (!TryCreateDescriptorPoolBlock(targetAllocations, layouts, perAllocationPoolSizes, out ComputeDescriptorPoolBlock? newBlock))
            return false;

        blocks.Add(newBlock);

        Result result = TryAllocateDescriptorSetsFromPool(newBlock.Pool, layouts, out descriptorSets);
        if (result != Result.Success)
            return false;

        newBlock.AllocatedAllocations++;
        return true;
    }

    private bool TryCreateDescriptorPoolBlock(
        uint allocationCapacity,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] perAllocationPoolSizes,
        out ComputeDescriptorPoolBlock? block)
    {
        block = null;

        DescriptorPoolSize[] poolSizes = new DescriptorPoolSize[perAllocationPoolSizes.Length];
        for (int i = 0; i < perAllocationPoolSizes.Length; i++)
        {
            DescriptorPoolSize size = perAllocationPoolSizes[i];
            uint baseCount = Math.Max(size.DescriptorCount, 1u);
            poolSizes[i] = new DescriptorPoolSize
            {
                Type = size.Type,
                DescriptorCount = baseCount * allocationCapacity
            };
        }

        fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
        {
            DescriptorPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                PoolSizeCount = (uint)poolSizes.Length,
                PPoolSizes = poolSizesPtr,
                MaxSets = allocationCapacity * (uint)layouts.Length
            };

            if (Api!.CreateDescriptorPool(device, ref poolInfo, null, out DescriptorPool descriptorPool) != Result.Success)
                return false;

            block = new ComputeDescriptorPoolBlock
            {
                Pool = descriptorPool,
                MaxAllocations = allocationCapacity,
                AllocatedAllocations = 0
            };

            return true;
        }
    }

    private Result TryAllocateDescriptorSetsFromPool(
        DescriptorPool descriptorPool,
        DescriptorSetLayout[] layouts,
        out DescriptorSet[] descriptorSets)
    {
        descriptorSets = new DescriptorSet[layouts.Length];

        fixed (DescriptorSetLayout* layoutPtr = layouts)
        fixed (DescriptorSet* setPtr = descriptorSets)
        {
            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = (uint)layouts.Length,
                PSetLayouts = layoutPtr
            };

            return Api!.AllocateDescriptorSets(device, ref allocInfo, setPtr);
        }
    }
}
