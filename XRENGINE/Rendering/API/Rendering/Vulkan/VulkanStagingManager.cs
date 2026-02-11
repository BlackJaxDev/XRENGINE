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
                if (entry.IdleFrames >= IdleFramesBeforeEviction || _entries.Count > MaxPoolEntries)
                {
                    renderer.DestroyBufferRaw(entry.Buffer, entry.Memory);
                    _entries.RemoveAt(i);
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
