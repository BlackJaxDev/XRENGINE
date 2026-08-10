using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Resource-owned compatibility operations for legacy renderer buffer APIs.</summary>
internal sealed unsafe partial class VulkanResourceRuntime
{
    private VulkanBackendObjectContext BufferContext
        => BackendObjectContext ?? throw new InvalidOperationException("The resource runtime has no backend context.");

    internal ulong GetBufferAllocationOffset(Buffer buffer)
        => Buffers.GetAllocationOffset(buffer);

    internal bool TryMapBufferMemory(Buffer buffer, DeviceMemory memory, ulong offset, ulong length, out void* mapped)
        => Buffers.TryMap(BufferContext, buffer, memory, offset, length, out mapped);

    internal void UnmapBufferMemory(Buffer buffer, DeviceMemory memory)
        => Buffers.Unmap(BufferContext, buffer, memory);

    internal void UpdateBufferMemory(Buffer buffer, DeviceMemory memory, ulong offset, ulong length, void* source)
    {
        if (!TryMapBufferMemory(buffer, memory, offset, length, out void* mapped))
            throw new InvalidOperationException("Failed to map Vulkan buffer memory.");

        Unsafe.CopyBlock(mapped, source, checked((uint)length));
        Buffers.Flush(BufferContext, buffer, memory, GetBufferAllocationOffset(buffer) + offset, length);
        UnmapBufferMemory(buffer, memory);
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
