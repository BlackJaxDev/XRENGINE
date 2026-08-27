using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanStagingManager
{
    private readonly object _sync = new();
    private readonly object _foregroundReserveProvisionSync = new();
    private readonly List<StagingBufferEntry> _entries = [];

    /// <summary>
    /// Maximum number of idle staging buffers to keep in the pool. Excess entries are
    /// destroyed when <see cref="Trim"/> is called (typically once per frame).
    /// </summary>
    private const int MaxPoolEntries = 32;

    /// <summary>
    /// Maximum idle staging bytes to keep resident before aggressive eviction starts.
    /// </summary>
    private const ulong IdleBytesWatermark = 256UL * 1024UL * 1024UL;

    /// <summary>
    /// Run full trim at least once every N calls even if memory watermark is not exceeded.
    /// </summary>
    private const int TrimIntervalFrames = 8;

    /// <summary>
    /// Number of consecutive <see cref="Trim"/> calls an idle buffer must survive before
    /// it becomes eligible for eviction.
    /// </summary>
    private const int IdleFramesBeforeEviction = 3;

    /// <summary>Maximum individual imported-upload staging allocation.</summary>
    internal const ulong ForegroundChunkCapacity = 4UL * 1024UL * 1024UL;

    private const int ForegroundReservedBufferCount = 4;
    private const MemoryPropertyFlags ForegroundReserveProperties =
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

    private sealed class StagingBufferEntry
    {
        public Buffer Buffer;
        public DeviceMemory Memory;
        public ulong Size;
        public BufferUsageFlags Usage;
        public MemoryPropertyFlags Properties;
        public ulong AllocationGeneration;
        public EVulkanStagingBufferState State;
        public bool ForegroundReserved;
        /// <summary>Number of <see cref="Trim"/> calls this entry has been idle.</summary>
        public int IdleFrames;
    }

    private int _trimFrameCounter;

    public bool CanPool(BufferUsageFlags usage, MemoryPropertyFlags properties)
    {
        // Upload staging: TransferSrc + HostVisible + HostCoherent
        if (usage == BufferUsageFlags.TransferSrcBit &&
            properties.HasFlag(MemoryPropertyFlags.HostVisibleBit) &&
            properties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            return true;

        // Readback staging: TransferDst + HostVisible + HostCached
        if (usage == BufferUsageFlags.TransferDstBit &&
            properties.HasFlag(MemoryPropertyFlags.HostVisibleBit) &&
            properties.HasFlag(MemoryPropertyFlags.HostCachedBit))
            return true;

        return false;
    }

    public unsafe (Buffer buffer, DeviceMemory memory) Acquire(
        VulkanBackendObjectContext context,
        ulong requestedSize,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        VoidPtr data,
        bool foregroundRequired = false)
    {
        if (requestedSize == 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSize), "Staging buffers must be at least 1 byte.");

        StagingBufferEntry? entry;
        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            entry = TryTakeReusable(requestedSize, usage, properties, foregroundRequired);
            if (entry is not null)
            {
                ulong publishedGeneration = context.Resources.GetPublishedGeneration(
                    ObjectType.Buffer,
                    entry.Buffer.Handle);
                if (publishedGeneration == 0 ||
                    publishedGeneration != entry.AllocationGeneration)
                {
                    throw new InvalidOperationException(
                        $"Vulkan staging allocation 0x{entry.Buffer.Handle:X} generation " +
                        $"{entry.AllocationGeneration} is not the published generation " +
                        $"{publishedGeneration}.");
                }

                entry.State = EVulkanStagingBufferState.InUse;
                entry.IdleFrames = 0;
            }
        }

        if (entry is null)
        {
            // Allocation may wait on Vulkan memory. Never hold the pool lock
            // across that native boundary; retirement can then progress.
            entry = CreateEntry(
                context,
                requestedSize,
                usage,
                properties,
                EVulkanStagingBufferState.InUse,
                foregroundReserved: false);
            AddCreatedEntry(context, entry);
        }

        if (data != null)
        {
            context.Resources.Buffers.UpdateFromVoidPtr(
                context, entry.Buffer, entry.Memory, 0, requestedSize, data);
        }

        return (entry.Buffer, entry.Memory);
    }

    /// <summary>
    /// Establishes a small dedicated PresentNow lane. Background uploads never
    /// take these buffers, so a streaming burst cannot starve visible content.
    /// Call only at a safe boundary, before foreground readiness starts.
    /// </summary>
    public unsafe void EnsureForegroundReserve(VulkanBackendObjectContext context)
    {
        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (CountForegroundReserveEntriesNoLock() >= ForegroundReservedBufferCount)
                return;
        }

        // Ensure may be pumped repeatedly while foreground uploads are active.
        // Serialize only the cold provisioning path and count all protected
        // entries, including in-flight ones, so activity cannot cause reserve
        // growth beyond the configured total.
        using (VulkanFrameLockScope.Enter(
                   _foregroundReserveProvisionSync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            int createdCount = 0;
            while (true)
            {
                using (VulkanFrameLockScope.Enter(
                           _sync,
                           EVulkanFrameWaitReason.UploadLock))
                {
                    if (CountForegroundReserveEntriesNoLock() >= ForegroundReservedBufferCount)
                        break;
                }

                StagingBufferEntry entry = CreateEntry(
                    context,
                    ForegroundChunkCapacity,
                    BufferUsageFlags.TransferSrcBit,
                    ForegroundReserveProperties,
                    EVulkanStagingBufferState.Idle,
                    foregroundReserved: true);
                AddCreatedEntry(context, entry);
                createdCount++;
            }

            VulkanForegroundStagingReserveSnapshot snapshot;
            using (VulkanFrameLockScope.Enter(
                       _sync,
                       EVulkanFrameWaitReason.UploadLock))
                snapshot = CaptureForegroundReserveSnapshotNoLock();

            if (snapshot.TotalCount < ForegroundReservedBufferCount ||
                snapshot.DistinctBufferCount != snapshot.TotalCount ||
                snapshot.DistinctGenerationCount != snapshot.TotalCount ||
                snapshot.IdleCount + snapshot.InUseCount + snapshot.RetiringCount != snapshot.TotalCount)
            {
                throw new InvalidOperationException(
                    "Vulkan foreground staging reserve provisioning did not publish distinct valid slices: " +
                    $"configured={snapshot.ConfiguredCount}, total={snapshot.TotalCount}, " +
                    $"idle={snapshot.IdleCount}, inUse={snapshot.InUseCount}, retiring={snapshot.RetiringCount}, " +
                    $"distinctBuffers={snapshot.DistinctBufferCount}, " +
                    $"distinctGenerations={snapshot.DistinctGenerationCount}.");
            }

            if (createdCount != 0)
            {
                Debug.Vulkan(
                    $"[Vulkan.StagingReserve] configured={snapshot.ConfiguredCount} created={createdCount} " +
                    $"total={snapshot.TotalCount} idle={snapshot.IdleCount} inUse={snapshot.InUseCount} " +
                    $"retiring={snapshot.RetiringCount} distinctBuffers={snapshot.DistinctBufferCount} " +
                    $"distinctGenerations={snapshot.DistinctGenerationCount} " +
                    $"identity=0x{snapshot.IdentitySignature:X16}");
            }
        }
    }

    /// <summary>
    /// Atomically advances a completed staging allocation to a fresh resource
    /// generation and publishes it as idle. Holding the pool lock across lifetime
    /// reactivation prevents <see cref="Acquire"/> from observing the allocation
    /// between those two publications.
    /// </summary>
    public bool TryPublishRecycled(
        VulkanResourceRuntime resources,
        Buffer buffer,
        DeviceMemory memory,
        ulong retiredAllocationGeneration,
        out ulong publishedAllocationGeneration)
    {
        publishedAllocationGeneration = 0;
        if (retiredAllocationGeneration == 0)
            throw new ArgumentOutOfRangeException(
                nameof(retiredAllocationGeneration),
                "A recycled staging allocation must identify its retired generation.");

        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            foreach (StagingBufferEntry entry in _entries)
            {
                if (entry.Buffer.Handle != buffer.Handle)
                    continue;
                if (entry.Memory.Handle != memory.Handle ||
                    entry.AllocationGeneration != retiredAllocationGeneration)
                {
                    throw new InvalidOperationException(
                        $"Vulkan staging recycle identity mismatch for buffer 0x{buffer.Handle:X}: " +
                        $"entryMemory=0x{entry.Memory.Handle:X}, retiredMemory=0x{memory.Handle:X}, " +
                        $"entryGeneration={entry.AllocationGeneration}, " +
                        $"retiredGeneration={retiredAllocationGeneration}.");
                }
                if (entry.State != EVulkanStagingBufferState.InUse)
                {
                    throw new InvalidOperationException(
                        $"Cannot recycle Vulkan staging allocation 0x{buffer.Handle:X} " +
                        $"generation {retiredAllocationGeneration} from state {entry.State}.");
                }

                entry.State = EVulkanStagingBufferState.Retiring;
                if (!resources.TryReactivateResourceAfterRetirement(
                        ObjectType.Buffer,
                        buffer.Handle,
                        retiredAllocationGeneration,
                        "StagingPool.Reuse",
                        out publishedAllocationGeneration))
                {
                    return false;
                }

                entry.AllocationGeneration = publishedAllocationGeneration;
                entry.State = EVulkanStagingBufferState.Idle;
                entry.IdleFrames = 0;
                return true;
            }
        }

        return false;
    }

    public bool TryForget(Buffer buffer, DeviceMemory memory)
    {
        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                StagingBufferEntry entry = _entries[i];
                if (entry.Buffer.Handle != buffer.Handle)
                    continue;

                if (memory.Handle != 0 && entry.Memory.Handle != memory.Handle)
                    continue;

                _entries.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    public void Destroy(VulkanBackendObjectContext context)
    {
        StagingBufferEntry[] entries;
        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            entries = [.. _entries];
            _entries.Clear();
        }

        foreach (StagingBufferEntry entry in entries)
            context.Resources.Buffers.Destroy(context, entry.Buffer, entry.Memory, "VulkanStagingManager.Destroy");
    }

    /// <summary>
    /// Evicts idle staging buffers that have exceeded <see cref="IdleFramesBeforeEviction"/>
    /// consecutive idle frames or that exceed <see cref="MaxPoolEntries"/> total pool size.
    /// Call once per frame (e.g. after command buffer submission).
    /// </summary>
    public void Trim(VulkanBackendObjectContext backendContext)
    {
        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            _trimFrameCounter++;

            ulong idleBytes = 0;
            for (int index = 0; index < _entries.Count; index++)
            {
                if (_entries[index].State == EVulkanStagingBufferState.Idle)
                    idleBytes += _entries[index].Size;
            }

            bool overEntryBudget = _entries.Count > MaxPoolEntries;
            bool overIdleBytesBudget = idleBytes > IdleBytesWatermark;
            bool intervalReached = _trimFrameCounter >= TrimIntervalFrames;
            if (!overEntryBudget && !overIdleBytesBudget && !intervalReached)
                return;

            _trimFrameCounter = 0;
            List<StagingBufferEntry>? evicted = null;
            for (int index = _entries.Count - 1; index >= 0; index--)
            {
                StagingBufferEntry entry = _entries[index];
                if (entry.State != EVulkanStagingBufferState.Idle || entry.ForegroundReserved)
                {
                    entry.IdleFrames = 0;
                    continue;
                }

                entry.IdleFrames++;
                if (entry.IdleFrames < IdleFramesBeforeEviction &&
                    _entries.Count <= MaxPoolEntries &&
                    idleBytes <= IdleBytesWatermark)
                {
                    continue;
                }

                evicted ??= [];
                evicted.Add(entry);
                _entries.RemoveAt(index);
                idleBytes = idleBytes > entry.Size
                    ? idleBytes - entry.Size
                    : 0;
            }

            if (evicted is null)
                return;

            for (int index = 0; index < evicted.Count; index++)
            {
                StagingBufferEntry entry = evicted[index];
                backendContext.Resources.Buffers.Destroy(
                    backendContext,
                    entry.Buffer,
                    entry.Memory,
                    "Staging.Trim");
            }
        }
    }

    private StagingBufferEntry? TryTakeReusable(
        ulong requestedSize,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        bool foregroundRequired)
    {
        StagingBufferEntry? best = null;
        ulong bestWaste = ulong.MaxValue;

        foreach (StagingBufferEntry entry in _entries)
        {
            if (entry.State != EVulkanStagingBufferState.Idle ||
                entry.Usage != usage ||
                entry.Properties != properties ||
                entry.Size < requestedSize)
                continue;
            if (entry.ForegroundReserved && !foregroundRequired)
                continue;

            ulong waste = entry.Size - requestedSize;
            if (waste < bestWaste)
            {
                bestWaste = waste;
                best = entry;
                if (waste == 0)
                    break;
            }
        }

        return best;
    }

    private static StagingBufferEntry CreateEntry(
        VulkanBackendObjectContext context,
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        EVulkanStagingBufferState state,
        bool foregroundReserved)
    {
        (Buffer buffer, DeviceMemory memory) = context.Resources.Buffers.CreateRaw(
            context,
            size,
            usage,
            properties,
            owner: "VulkanStagingManager");
        ulong allocationGeneration = context.Resources.GetPublishedGeneration(
            ObjectType.Buffer,
            buffer.Handle);
        if (allocationGeneration == 0)
        {
            context.Resources.Buffers.Destroy(
                context,
                buffer,
                memory,
                "VulkanStagingManager.MissingAllocationGeneration");
            throw new InvalidOperationException(
                $"Vulkan staging allocation 0x{buffer.Handle:X} was created without a published generation.");
        }

        return new StagingBufferEntry
        {
            Buffer = buffer,
            Memory = memory,
            Size = size,
            Usage = usage,
            Properties = properties,
            AllocationGeneration = allocationGeneration,
            State = state,
            ForegroundReserved = foregroundReserved,
        };
    }

    private void AddCreatedEntry(
        VulkanBackendObjectContext context,
        StagingBufferEntry entry)
    {
        try
        {
            using (VulkanFrameLockScope.Enter(
                       _sync,
                       EVulkanFrameWaitReason.UploadLock))
            {
                for (int index = 0; index < _entries.Count; index++)
                {
                    StagingBufferEntry existing = _entries[index];
                    if (existing.Buffer.Handle == entry.Buffer.Handle ||
                        existing.AllocationGeneration == entry.AllocationGeneration)
                    {
                        throw new InvalidOperationException(
                            "Vulkan staging allocation insertion would duplicate a live identity: " +
                            $"buffer=0x{entry.Buffer.Handle:X}, generation={entry.AllocationGeneration}.");
                    }
                }

                _entries.Add(entry);
            }
        }
        catch
        {
            context.Resources.Buffers.Destroy(
                context,
                entry.Buffer,
                entry.Memory,
                "VulkanStagingManager.RejectedEntry");
            throw;
        }
    }

    private int CountForegroundReserveEntriesNoLock()
    {
        int count = 0;
        for (int index = 0; index < _entries.Count; index++)
            if (IsForegroundReserveEntry(_entries[index]))
                count++;

        return count;
    }

    private VulkanForegroundStagingReserveSnapshot CaptureForegroundReserveSnapshotNoLock()
    {
        int totalCount = 0;
        int idleCount = 0;
        int inUseCount = 0;
        int retiringCount = 0;
        int distinctBufferCount = 0;
        int distinctGenerationCount = 0;
        ulong identitySignature = 14695981039346656037UL;

        for (int index = 0; index < _entries.Count; index++)
        {
            StagingBufferEntry entry = _entries[index];
            if (!IsForegroundReserveEntry(entry))
                continue;

            totalCount++;
            switch (entry.State)
            {
                case EVulkanStagingBufferState.Idle:
                    idleCount++;
                    break;
                case EVulkanStagingBufferState.InUse:
                    inUseCount++;
                    break;
                case EVulkanStagingBufferState.Retiring:
                    retiringCount++;
                    break;
            }

            bool bufferSeen = entry.Buffer.Handle == 0;
            bool generationSeen = entry.AllocationGeneration == 0;
            for (int priorIndex = 0; priorIndex < index && (!bufferSeen || !generationSeen); priorIndex++)
            {
                StagingBufferEntry prior = _entries[priorIndex];
                if (!IsForegroundReserveEntry(prior))
                    continue;

                bufferSeen |= prior.Buffer.Handle == entry.Buffer.Handle;
                generationSeen |= prior.AllocationGeneration == entry.AllocationGeneration;
            }

            if (!bufferSeen)
                distinctBufferCount++;
            if (!generationSeen)
                distinctGenerationCount++;

            identitySignature ^= entry.Buffer.Handle;
            identitySignature *= 1099511628211UL;
            identitySignature ^= entry.AllocationGeneration;
            identitySignature *= 1099511628211UL;
        }

        return new VulkanForegroundStagingReserveSnapshot(
            ForegroundReservedBufferCount,
            totalCount,
            idleCount,
            inUseCount,
            retiringCount,
            distinctBufferCount,
            distinctGenerationCount,
            identitySignature);
    }

    private static bool IsForegroundReserveEntry(StagingBufferEntry entry)
        => entry.ForegroundReserved &&
           entry.Usage == BufferUsageFlags.TransferSrcBit &&
           entry.Properties == ForegroundReserveProperties &&
           entry.Size >= ForegroundChunkCapacity;
}
