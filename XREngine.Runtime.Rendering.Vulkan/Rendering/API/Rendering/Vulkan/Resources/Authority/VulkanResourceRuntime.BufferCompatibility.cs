using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Resource-owned compatibility operations for legacy renderer buffer APIs.</summary>
internal sealed partial class VulkanResourceRuntime
{
    private VulkanBackendObjectContext BufferContext
        => BackendObjectContext ?? throw new InvalidOperationException("The resource runtime has no backend context.");

    internal ulong GetBufferAllocationOffset(Buffer buffer)
        => Buffers.GetAllocationOffset(buffer);

    internal unsafe void UpdateBufferMemory(Buffer buffer, DeviceMemory memory, ulong offset, ulong length, void* source)
    {
        if (!Buffers.TryCreateMappedSlice(BufferContext, buffer, memory, offset, length, out VulkanMappedMemorySlice slice) ||
            !Buffers.TryAcquireWrite(BufferContext, in slice, out VulkanMappedMemoryWriteLease lease))
        {
            throw new InvalidOperationException("Failed to acquire a Vulkan mapped-memory write lease.");
        }

        using (lease)
            new ReadOnlySpan<byte>(source, checked((int)length)).CopyTo(lease.Bytes);
    }

    internal void DestroyBufferRaw(Buffer buffer, DeviceMemory memory, string owner)
    {
        if (buffer.Handle != 0)
            Allocations.Staging.TryForget(buffer, memory);
        Buffers.Destroy(BufferContext, buffer, memory, owner);
    }

    internal void DestroyRemainingTrackedMeshUniformBuffers()
    {
        KeyValuePair<ulong, DeviceMemory>[] remaining =
            Allocations.Buffers.DrainMeshUniformBuffers();
        foreach (KeyValuePair<ulong, DeviceMemory> entry in remaining)
        {
            Buffer buffer = new() { Handle = entry.Key };
            DestroyBufferRaw(buffer, entry.Value, "MeshUniformBuffer.Teardown");
        }
    }
}
