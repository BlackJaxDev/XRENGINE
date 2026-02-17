using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal unsafe sealed class VulkanStagingManager
{
    private readonly object _sync = new();
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

    private sealed class StagingBufferEntry
    {
        public Buffer Buffer;
        public DeviceMemory Memory;
        public ulong Size;
        public BufferUsageFlags Usage;
        public MemoryPropertyFlags Properties;
        public bool InUse;
        /// <summary>Number of <see cref="Trim"/> calls this entry has been idle.</summary>
        public int IdleFrames;
    }

    private int _trimFrameCounter;

    public bool CanPool(BufferUsageFlags usage, MemoryPropertyFlags properties)
        => usage == BufferUsageFlags.TransferSrcBit &&
           properties.HasFlag(MemoryPropertyFlags.HostVisibleBit) &&
           properties.HasFlag(MemoryPropertyFlags.HostCoherentBit);

    public (Buffer buffer, DeviceMemory memory) Acquire(
        VulkanRenderer renderer,
        ulong requestedSize,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        VoidPtr data)
    {
        if (requestedSize == 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSize), "Staging buffers must be at least 1 byte.");

        lock (_sync)
        {
            StagingBufferEntry? entry = TryTakeReusable(requestedSize, usage, properties);
            if (entry is null)
            {
                (Buffer buffer, DeviceMemory memory) = renderer.CreateBufferRaw(requestedSize, usage, properties);
                entry = new StagingBufferEntry
                {
                    Buffer = buffer,
                    Memory = memory,
                    Size = requestedSize,
                    Usage = usage,
                    Properties = properties,
                    InUse = true
                };
                _entries.Add(entry);
            }
            else
            {
                entry.InUse = true;
                entry.IdleFrames = 0;
            }

            if (data != null)
                renderer.UploadBufferMemory(entry.Memory, requestedSize, data.Pointer);

            return (entry.Buffer, entry.Memory);
        }
    }

    public bool TryRelease(Buffer buffer, DeviceMemory memory)
    {
        lock (_sync)
        {
            foreach (StagingBufferEntry entry in _entries)
            {
                if (entry.Buffer.Handle != buffer.Handle || entry.Memory.Handle != memory.Handle)
                    continue;

                entry.InUse = false;
                return true;
            }
        }

        return false;
    }

    public void Destroy(VulkanRenderer renderer)
    {
        lock (_sync)
        {
            foreach (StagingBufferEntry entry in _entries)
                renderer.DestroyBufferRaw(entry.Buffer, entry.Memory);

            _entries.Clear();
        }
    }

    /// <summary>
    /// Evicts idle staging buffers that have exceeded <see cref="IdleFramesBeforeEviction"/>
    /// consecutive idle frames or that exceed <see cref="MaxPoolEntries"/> total pool size.
    /// Call once per frame (e.g. after command buffer submission).
    /// </summary>
    public void Trim(VulkanRenderer renderer)
    {
        lock (_sync)
        {
            _trimFrameCounter++;

            ulong idleBytes = 0;
            int idleEntries = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                StagingBufferEntry entry = _entries[i];
                if (entry.InUse)
                    continue;

                idleEntries++;
                idleBytes += entry.Size;
            }

            bool overEntryBudget = _entries.Count > MaxPoolEntries;
            bool overIdleBytesBudget = idleBytes > IdleBytesWatermark;
            bool intervalReached = _trimFrameCounter >= TrimIntervalFrames;
            if (!overEntryBudget && !overIdleBytesBudget && !intervalReached)
                return;

            _trimFrameCounter = 0;

            // Increment idle counters and collect eviction candidates.
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                StagingBufferEntry entry = _entries[i];
                if (entry.InUse)
                {
                    entry.IdleFrames = 0;
                    continue;
                }

                entry.IdleFrames++;

                bool entryOldEnough = entry.IdleFrames >= IdleFramesBeforeEviction;
                bool stillOverEntryBudget = _entries.Count > MaxPoolEntries;
                bool stillOverIdleBudget = idleBytes > IdleBytesWatermark;
                if (entryOldEnough || stillOverEntryBudget || stillOverIdleBudget)
                {
                    renderer.DestroyBufferRaw(entry.Buffer, entry.Memory);
                    _entries.RemoveAt(i);
                    if (idleEntries > 0)
                        idleEntries--;
                    idleBytes = idleBytes > entry.Size ? idleBytes - entry.Size : 0;
                }
            }
        }
    }

    private StagingBufferEntry? TryTakeReusable(
        ulong requestedSize,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties)
    {
        StagingBufferEntry? best = null;
        ulong bestWaste = ulong.MaxValue;

        foreach (StagingBufferEntry entry in _entries)
        {
            if (entry.InUse || entry.Usage != usage || entry.Properties != properties || entry.Size < requestedSize)
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
}
