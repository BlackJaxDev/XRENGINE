using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal void TrackMeshUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle == 0)
            return;

        _bufferResourceManager.TrackMeshUniformBuffer(buffer, memory);
    }

    internal void DestroyTrackedMeshUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle == 0 && memory.Handle == 0)
            return;

        if (buffer.Handle != 0)
            _bufferResourceManager.RemoveMeshUniformBuffer(buffer);

        if (buffer.Handle != 0)
            RetireBuffer(buffer, memory);
        else if (memory.Handle != 0)
            RetireBuffer(default, memory);
    }

    private void DestroyRemainingTrackedMeshUniformBuffers()
    {
        KeyValuePair<ulong, DeviceMemory>[] remaining =
            _bufferResourceManager.DrainMeshUniformBuffers();

        foreach (KeyValuePair<ulong, DeviceMemory> entry in remaining)
        {
            Silk.NET.Vulkan.Buffer buffer = new() { Handle = entry.Key };
            DestroyBufferRaw(buffer, entry.Value);
        }
    }
}
