using Silk.NET.Vulkan;
using System.Collections.Concurrent;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly ConcurrentDictionary<ulong, BufferViewCreateInfo> _descriptorHeapBufferViewCreateInfos = new();

    internal void TrackDescriptorHeapBufferView(BufferView bufferView, in BufferViewCreateInfo createInfo)
    {
        if (bufferView.Handle == 0)
            return;

        _descriptorHeapBufferViewCreateInfos[bufferView.Handle] = createInfo with { PNext = null };
        RegisterVulkanBufferViewResource(bufferView, createInfo.Buffer, "BufferView");
    }

    internal void UntrackDescriptorHeapBufferView(BufferView bufferView)
    {
        if (bufferView.Handle != 0)
            _descriptorHeapBufferViewCreateInfos.TryRemove(bufferView.Handle, out _);
    }

    internal bool TryGetDescriptorHeapBufferViewCreateInfo(BufferView bufferView, out BufferViewCreateInfo createInfo)
    {
        if (bufferView.Handle != 0 &&
            _descriptorHeapBufferViewCreateInfos.TryGetValue(bufferView.Handle, out createInfo))
        {
            return true;
        }

        createInfo = default;
        return false;
    }
}
