using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    public (Buffer buffer, DeviceMemory memory) Acquire(
        VulkanBackendObjectContext context,
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
                (Buffer buffer, DeviceMemory memory) = context.Resources.Buffers.CreateRaw(
                    context,
                    requestedSize,
                    usage,
                    properties,
                    owner: "VulkanStagingManager");
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
            {
                if (!context.Resources.Buffers.TryMap(context, entry.Buffer, entry.Memory, 0, requestedSize, out void* mapped))
                    throw new InvalidOperationException("Failed to map Vulkan staging buffer memory.");
                try
                {
                    Unsafe.CopyBlock(mapped, data.Pointer, checked((uint)requestedSize));
                    context.Resources.Buffers.Flush(context, entry.Buffer, entry.Memory, 0, requestedSize);
                }
                finally
                {
                    context.Resources.Buffers.Unmap(context, entry.Buffer, entry.Memory);
                }
            }

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

    public bool TryForget(Buffer buffer, DeviceMemory memory)
    {
        lock (_sync)
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
        lock (_sync)
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
        lock (_sync)
        {
            _trimFrameCounter++;

            ulong idleBytes = 0;
            for (int index = 0; index < _entries.Count; index++)
            {
                if (!_entries[index].InUse)
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
                if (entry.InUse)
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
