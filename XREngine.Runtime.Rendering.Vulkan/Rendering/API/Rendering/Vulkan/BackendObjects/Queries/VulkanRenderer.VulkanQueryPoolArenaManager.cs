using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Bounded renderer-owned native query-pool arenas. Query wrappers retain
    /// immutable slot ranges instead of creating or reconfiguring native pools.
    /// </summary>
    private sealed class VulkanQueryPoolArenaManager(VulkanRenderer renderer)
    {
        private sealed class Chunk(
            QueryPool pool,
            uint identity,
            uint capacity,
            VulkanQueryPoolKey key)
        {
            public readonly QueryPool Pool = pool;
            public readonly uint Identity = identity;
            public readonly uint Capacity = capacity;
            public readonly VulkanQueryPoolKey Key = key;
            public readonly RenderQuerySlotAllocator Slots = new(capacity);
        }

        private sealed class Arena
        {
            public readonly List<Chunk> Chunks = [];
        }

        private readonly object _lock = new();
        private readonly Dictionary<VulkanQueryPoolKey, Arena> _arenas = [];
        private uint _nextPoolIdentity;
        private uint _poolCount;
        private uint _capacity;
        private uint _allocatedSlots;
        private uint _highWaterSlots;
        private ulong _allocationCount;
        private ulong _releaseCount;
        private ulong _growthCount;
        private ulong _exhaustionCount;
        private ulong _resetEpochCount;
        private ulong _retiredPoolCount;
        private bool _disposed;

        public bool TryAllocate(
            in VulkanQueryPoolKey key,
            uint queryCount,
            out VulkanQueryPoolAllocation allocation,
            out string? reason)
        {
            allocation = default;
            reason = null;
            if (queryCount == 0u)
            {
                reason = "Query ranges must contain at least one slot.";
                return false;
            }

            lock (_lock)
            {
                if (_disposed || renderer.IsDeviceLost || !renderer.IsLogicalDeviceReady)
                {
                    reason = "The Vulkan query arena is unavailable because the device is lost or shutting down.";
                    return false;
                }

                if (!_arenas.TryGetValue(key, out Arena? arena))
                {
                    arena = new Arena();
                    _arenas.Add(key, arena);
                }

                for (int index = 0; index < arena.Chunks.Count; index++)
                {
                    Chunk chunk = arena.Chunks[index];
                    if (TryOccupyRange(chunk, queryCount, out uint firstQuery))
                    {
                        allocation = CreateAllocation(chunk, firstQuery, queryCount);
                        return true;
                    }
                }

                if (!VulkanQueryArenaPolicy.CanGrow(arena.Chunks.Count))
                {
                    _exhaustionCount++;
                    reason = $"Query arena exhausted its bounded {VulkanQueryArenaPolicy.MaxChunksPerKey}-chunk policy for {key.QueryType}.";
                    return false;
                }

                uint chunkCapacity = VulkanQueryArenaPolicy.ResolveChunkCapacity(queryCount);
                if (!TryCreateChunk(key, chunkCapacity, out Chunk? created, out reason))
                    return false;

                arena.Chunks.Add(created);
                _growthCount++;
                if (!TryOccupyRange(created, queryCount, out uint allocatedFirst))
                {
                    _exhaustionCount++;
                    reason = "A newly created query-pool chunk could not satisfy its requested contiguous range.";
                    return false;
                }

                allocation = CreateAllocation(created, allocatedFirst, queryCount);
                return true;
            }
        }

        public void Release(in VulkanQueryPoolAllocation allocation)
        {
            if (!allocation.IsValid)
                return;

            lock (_lock)
            {
                if (!_arenas.TryGetValue(allocation.Key, out Arena? arena))
                    return;

                for (int index = 0; index < arena.Chunks.Count; index++)
                {
                    Chunk chunk = arena.Chunks[index];
                    if (chunk.Identity != allocation.PoolIdentity || chunk.Pool.Handle != allocation.Pool.Handle)
                        continue;

                    if (!chunk.Slots.Release(allocation.FirstQuery, allocation.QueryCount))
                        return;
                    _allocatedSlots -= allocation.QueryCount;
                    _releaseCount++;
                    return;
                }
            }
        }

        public QueryArenaTelemetry CaptureTelemetry()
        {
            lock (_lock)
            {
                return new(
                    _poolCount,
                    _capacity,
                    _allocatedSlots,
                    _highWaterSlots,
                    _allocationCount,
                    _releaseCount,
                    _growthCount,
                    _exhaustionCount,
                    _resetEpochCount,
                    _retiredPoolCount);
            }
        }

        public void RecordResetEpoch()
        {
            lock (_lock)
                _resetEpochCount++;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;

                foreach (Arena arena in _arenas.Values)
                {
                    for (int index = 0; index < arena.Chunks.Count; index++)
                    {
                        QueryPool pool = arena.Chunks[index].Pool;
                        if (pool.Handle != 0)
                        {
                            renderer.RetireQueryPool(pool);
                            _retiredPoolCount++;
                        }
                    }
                }
                _arenas.Clear();
                _poolCount = 0u;
                _capacity = 0u;
                _allocatedSlots = 0u;
            }
        }

        private bool TryCreateChunk(
            in VulkanQueryPoolKey key,
            uint capacity,
            out Chunk chunk,
            out string? reason)
        {
            QueryPoolCreateInfo createInfo = new()
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = key.QueryType,
                QueryCount = capacity,
                PipelineStatistics = key.PipelineStatistics,
            };
            Result result = renderer.Api!.CreateQueryPool(renderer.Device, ref createInfo, null, out QueryPool pool);
            if (result != Result.Success)
            {
                chunk = null!;
                reason = $"vkCreateQueryPool failed for {key.QueryType}: {result}.";
                return false;
            }

            uint identity = ++_nextPoolIdentity;
            if (identity == 0u)
                identity = ++_nextPoolIdentity;
            chunk = new Chunk(pool, identity, capacity, key);
            renderer.RegisterVulkanResource(ObjectType.QueryPool, pool.Handle, $"QueryArena.{key.QueryType}.{identity}");
            _poolCount++;
            _capacity += capacity;
            reason = null;
            return true;
        }

        private VulkanQueryPoolAllocation CreateAllocation(Chunk chunk, uint firstQuery, uint queryCount)
        {
            _allocationCount++;
            _allocatedSlots += queryCount;
            _highWaterSlots = Math.Max(_highWaterSlots, _allocatedSlots);
            return new(
                chunk.Pool,
                chunk.Identity,
                firstQuery,
                queryCount,
                chunk.Capacity,
                chunk.Key);
        }

        private static bool TryOccupyRange(Chunk chunk, uint queryCount, out uint firstQuery)
        {
            firstQuery = 0u;
            return chunk.Slots.TryAllocate(queryCount, out firstQuery);
        }

    }
}
