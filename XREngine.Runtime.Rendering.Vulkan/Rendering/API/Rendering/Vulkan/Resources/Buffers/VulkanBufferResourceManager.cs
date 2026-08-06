using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Vulkan buffer allocation registries and the selected memory allocator.
/// Renderer buffer operations are behavior-only clients of this state.
/// </summary>
internal sealed class VulkanBufferResourceManager
{
    internal IVulkanMemoryAllocator? MemoryAllocator { get; set; }
    internal ConcurrentDictionary<ulong, VulkanMemoryAllocation> Allocations { get; } = new();
    internal ConcurrentDictionary<ulong, VulkanMemoryAllocation> LegacyAllocations { get; } = new();
    internal ConcurrentDictionary<ulong, byte> LiveHandles { get; } = new();
    private readonly object _meshUniformBuffersLock = new();
    private readonly Dictionary<ulong, DeviceMemory> _meshUniformBuffers = [];

    internal void TrackMeshUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
    {
        lock (_meshUniformBuffersLock)
            _meshUniformBuffers[buffer.Handle] = memory;
    }

    internal void RemoveMeshUniformBuffer(Silk.NET.Vulkan.Buffer buffer)
    {
        lock (_meshUniformBuffersLock)
            _meshUniformBuffers.Remove(buffer.Handle);
    }

    internal KeyValuePair<ulong, DeviceMemory>[] DrainMeshUniformBuffers()
    {
        lock (_meshUniformBuffersLock)
        {
            if (_meshUniformBuffers.Count == 0)
                return [];

            KeyValuePair<ulong, DeviceMemory>[] remaining = [.. _meshUniformBuffers];
            _meshUniformBuffers.Clear();
            return remaining;
        }
    }

    /// <summary>
    /// Registers a dedicated mapped-frame chunk with the same allocation and live-handle
    /// registries used by renderer-owned raw buffers. The frame arena performs native teardown
    /// only after a device-idle proof, so this intentionally does not enter the frame retirement
    /// queue whose owner is the renderer command lifetime subsystem.
    /// </summary>
    internal void RegisterMappedFrameArenaChunk(
        Silk.NET.Vulkan.Buffer buffer,
        in VulkanMemoryAllocation allocation)
    {
        if (buffer.Handle == 0 || allocation.Memory.Handle == 0)
            throw new ArgumentException("Mapped frame chunks require live buffer and memory handles.");

        if (!LiveHandles.TryAdd(buffer.Handle, 0))
            throw new InvalidOperationException($"Mapped frame chunk buffer 0x{buffer.Handle:X} was already registered.");
        if (LegacyAllocations.TryAdd(buffer.Handle, allocation))
            return;

        LiveHandles.TryRemove(buffer.Handle, out _);
        throw new InvalidOperationException($"Mapped frame chunk allocation 0x{buffer.Handle:X} was already registered.");
    }

    internal bool TryUnregisterMappedFrameArenaChunk(
        Silk.NET.Vulkan.Buffer buffer,
        out VulkanMemoryAllocation allocation)
    {
        LiveHandles.TryRemove(buffer.Handle, out _);
        return LegacyAllocations.TryRemove(buffer.Handle, out allocation);
    }
}
