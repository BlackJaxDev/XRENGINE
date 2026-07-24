using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _computeDescriptorCacheLock = new();

    /// <summary>
    /// Represents the array of compute descriptor image caches, one for each swapchain image.
    /// </summary>
    private ComputeDescriptorImageCache[]? _computeDescriptorCaches;

    /// <summary>
    /// Initializes the compute descriptor caches for the specified number of images.
    /// </summary>
    /// <param name="imageCount">The number of images for which to initialize the compute descriptor caches.</param>
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

    /// <summary>
    /// Ensures that the compute descriptor cache array has the specified capacity, resizing it if necessary.
    /// </summary>
    /// <param name="imageCount">The desired number of compute descriptor caches.</param>
    private void EnsureComputeDescriptorCacheCapacity(int imageCount)
    {
        if (imageCount <= 0)
            return;

        lock (_computeDescriptorCacheLock)
        {
            if (_computeDescriptorCaches is null)
            {
                _computeDescriptorCaches = new ComputeDescriptorImageCache[imageCount];
                for (int i = 0; i < imageCount; i++)
                    _computeDescriptorCaches[i] = new ComputeDescriptorImageCache();
                return;
            }

            if (_computeDescriptorCaches.Length >= imageCount)
                return;

            int oldLength = _computeDescriptorCaches.Length;
            Array.Resize(ref _computeDescriptorCaches, imageCount);
            for (int i = oldLength; i < _computeDescriptorCaches.Length; i++)
                _computeDescriptorCaches[i] = new ComputeDescriptorImageCache();
        }
    }

    /// <summary>
    /// Destroys all compute descriptor caches, releasing their associated resources.
    /// </summary>
    private void DestroyComputeDescriptorCaches()
    {
        lock (_computeDescriptorCacheLock)
            DestroyComputeDescriptorCaches_NoLock();
    }

    /// <summary>
    /// Destroys all compute descriptor caches without acquiring the lock. 
    /// This method should only be called from within a lock on _computeDescriptorCacheLock.
    /// </summary>
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

                    RetireDescriptorPool(block.Pool);
                }
            }
        }

        _computeDescriptorCaches = null;
    }

    /// <summary>
    /// Releases references to compute descriptor sets that are associated with physical resources that are about to be destroyed.
    /// </summary>
    /// <returns>The number of compute descriptor pools that were retired as a result of this operation.</returns>
    internal int ReleaseComputeDescriptorReferencesForPhysicalResourceDestruction()
    {
        lock (_computeDescriptorCacheLock)
        {
            int imageCount = _computeDescriptorCaches?.Length ?? _commandBuffers?.Length ?? 0;
            int poolCount = RetireComputeDescriptorCaches_NoLock();

            if (imageCount > 0)
            {
                _computeDescriptorCaches = new ComputeDescriptorImageCache[imageCount];
                for (int i = 0; i < imageCount; i++)
                    _computeDescriptorCaches[i] = new ComputeDescriptorImageCache();
            }

            return poolCount;
        }
    }

    /// <summary>
    /// Retires all compute descriptor caches without acquiring the lock. 
    /// This method should only be called from within a lock on _computeDescriptorCacheLock.
    /// </summary>
    /// <returns>The number of compute descriptor pools that were retired as a result of this operation.</returns>
    private int RetireComputeDescriptorCaches_NoLock()
    {
        if (_computeDescriptorCaches is null)
            return 0;

        HashSet<ulong> retiredPools = [];
        foreach (ComputeDescriptorImageCache cache in _computeDescriptorCaches)
        {
            foreach (List<ComputeDescriptorPoolBlock> blocks in cache.PoolsBySchema.Values)
            {
                foreach (ComputeDescriptorPoolBlock block in blocks)
                {
                    if (block.Pool.Handle == 0 || !retiredPools.Add(block.Pool.Handle))
                        continue;

                    RetireDescriptorPool(block.Pool);
                }
            }
        }

        _computeDescriptorCaches = null;
        return retiredPools.Count;
    }

    /// <summary>
    /// Attempts to retrieve existing compute descriptor sets from the cache or create new ones if they do not exist.
    /// </summary>
    /// <param name="imageIndex">The index of the image for which to retrieve or create descriptor sets.</param>
    /// <param name="schemaKey">The key representing the schema of the compute descriptor set.</param>
    /// <param name="bindingKey">The key representing the binding within the compute descriptor set.</param>
    /// <param name="layouts">An array of descriptor set layouts to be used for allocation if new descriptor sets are created.</param>
    /// <param name="perAllocationPoolSizes">An array of descriptor pool sizes specifying the number of descriptors of each type to allocate per pool.</param>
    /// <param name="usesUpdateAfterBind">Indicates whether the descriptor sets use the update-after-bind feature.</param>
    /// <param name="descriptorSets">Outputs the retrieved or newly created descriptor sets.</param>
    /// <param name="isNewAllocation">Outputs whether the descriptor sets were newly allocated.</param>
    /// <returns>True if the descriptor sets were successfully retrieved or created; otherwise, false.</returns>
    internal bool TryGetOrCreateComputeDescriptorSets(
        uint imageIndex,
        ulong schemaKey,
        ulong bindingKey,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] perAllocationPoolSizes,
        bool usesUpdateAfterBind,
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

            ComputeDescriptorImageCache? cache = _computeDescriptorCaches[imageIndex];
            if (cache is null)
                return false;

            ComputeDescriptorCacheKey key = new(schemaKey, bindingKey);
            if (cache.CachedSets.TryGetValue(key, out DescriptorSet[]? cachedDescriptorSets) && cachedDescriptorSets is not null)
            {
                descriptorSets = cachedDescriptorSets;
                return true;
            }

            if (!TryAllocateDescriptorSetBatch(cache, schemaKey, layouts, perAllocationPoolSizes, usesUpdateAfterBind, out descriptorSets))
                return false;

            cache.CachedSets[key] = descriptorSets;
            isNewAllocation = true;
            return true;
        }
    }

    /// <summary>
    /// Attempts to allocate a batch of compute descriptor sets from the available descriptor pool blocks, creating a new pool block if necessary.
    /// </summary>
    /// <param name="cache">The cache containing the descriptor pool blocks for the current image.</param>
    /// <param name="schemaKey">The key representing the schema of the compute descriptor set.</param>
    /// <param name="layouts">An array of descriptor set layouts to be used for allocation.</param>
    /// <param name="perAllocationPoolSizes">An array of descriptor pool sizes specifying the number of descriptors of each type to allocate per pool.</param>
    /// <param name="usesUpdateAfterBind">Indicates whether the descriptor sets use the update-after-bind feature.</param>
    /// <param name="descriptorSets">Outputs the allocated descriptor sets if the allocation is successful.</param>
    /// <returns>True if the descriptor sets were successfully allocated; otherwise, false.</returns>
    private bool TryAllocateDescriptorSetBatch(
        ComputeDescriptorImageCache cache,
        ulong schemaKey,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] perAllocationPoolSizes,
        bool usesUpdateAfterBind,
        out DescriptorSet[] descriptorSets)
    {
        descriptorSets = Array.Empty<DescriptorSet>();
        const uint baseAllocationCapacity = 64;

        if (!cache.PoolsBySchema.TryGetValue(schemaKey, out List<ComputeDescriptorPoolBlock>? blocks))
        {
            blocks = [];
            cache.PoolsBySchema[schemaKey] = blocks;
        }

        foreach (ComputeDescriptorPoolBlock block in blocks)
        {
            if (block.UsesUpdateAfterBind != usesUpdateAfterBind)
                continue;

            if (block.AllocatedAllocations >= block.MaxAllocations)
                continue;

            Result allocResult = TryAllocateDescriptorSetsFromPool(
                block.Pool,
                layouts,
                block.UsesUpdateAfterBind,
                out descriptorSets);
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
            _ => Math.Min(blocks[^1].MaxAllocations * 2u, 512u)
        };

        if (!TryCreateDescriptorPoolBlock(targetAllocations, layouts, perAllocationPoolSizes, usesUpdateAfterBind, out ComputeDescriptorPoolBlock? newBlock))
            return false;

        if (newBlock is null)
            return false;

        blocks.Add(newBlock);

        Result result = TryAllocateDescriptorSetsFromPool(
            newBlock.Pool,
            layouts,
            newBlock.UsesUpdateAfterBind,
            out descriptorSets);
        if (result != Result.Success)
            return false;

        newBlock.AllocatedAllocations++;
        return true;
    }

    /// <summary>
    /// Attempts to create a new compute descriptor pool block with the specified allocation capacity and pool sizes.
    /// </summary>
    /// <param name="allocationCapacity">The number of descriptor sets the new pool block should be able to allocate.</param>
    /// <param name="layouts">An array of descriptor set layouts to be used for the pool block.</param>
    /// <param name="perAllocationPoolSizes">An array of descriptor pool sizes specifying the number of descriptors of each type to allocate per pool.</param>
    /// <param name="usesUpdateAfterBind">Indicates whether the descriptor sets in the pool block use the update-after-bind feature.</param>
    /// <param name="block">Outputs the newly created compute descriptor pool block if the creation is successful.</param>
    /// <returns>True if the pool block was successfully created; otherwise, false.</returns>
    private bool TryCreateDescriptorPoolBlock(
        uint allocationCapacity,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] perAllocationPoolSizes,
        bool usesUpdateAfterBind,
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
                Flags = usesUpdateAfterBind
                    ? DescriptorPoolCreateFlags.UpdateAfterBindBit
                    : 0,
                PoolSizeCount = (uint)poolSizes.Length,
                PPoolSizes = poolSizesPtr,
                MaxSets = allocationCapacity * (uint)layouts.Length
            };

            if (Api!.CreateDescriptorPool(device, ref poolInfo, null, out DescriptorPool descriptorPool) != Result.Success)
                return false;

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();

            block = new ComputeDescriptorPoolBlock
            {
                Pool = descriptorPool,
                MaxAllocations = allocationCapacity,
                AllocatedAllocations = 0,
                UsesUpdateAfterBind = usesUpdateAfterBind
            };

            return true;
        }
    }

    /// <summary>
    /// Attempts to allocate descriptor sets from the specified compute descriptor pool.
    /// </summary>
    /// <param name="descriptorPool">The compute descriptor pool from which to allocate the descriptor sets.</param>
    /// <param name="layouts">An array of descriptor set layouts specifying the layout of each descriptor set to allocate.</param>
    /// <param name="usesUpdateAfterBind">Indicates whether the allocated descriptor sets use the update-after-bind feature.</param>
    /// <param name="descriptorSets">Outputs the allocated descriptor sets if the allocation is successful.</param>
    /// <returns>A Vulkan result indicating the success or failure of the allocation operation.</returns>
    private Result TryAllocateDescriptorSetsFromPool(
        DescriptorPool descriptorPool,
        DescriptorSetLayout[] layouts,
        bool usesUpdateAfterBind,
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

            Result result = Api!.AllocateDescriptorSets(device, ref allocInfo, setPtr);
            if (result == Result.Success)
            {
                RegisterVulkanDescriptorSets(
                    descriptorPool,
                    descriptorSets,
                    usesUpdateAfterBind,
                    "Compute.DescriptorSet");
                SetDebugDescriptorSetNames(descriptorSets, "Compute.DescriptorSet");
                RecordVulkanDescriptorTableGeneration("ComputeDescriptorSets.Allocated");
            }

            return result;
        }
    }
}
