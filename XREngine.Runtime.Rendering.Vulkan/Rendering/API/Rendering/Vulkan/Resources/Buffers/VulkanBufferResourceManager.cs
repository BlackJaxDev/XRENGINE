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
}
