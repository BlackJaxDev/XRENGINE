using Silk.NET.Vulkan;
using System.Collections.Concurrent;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal void TrackDescriptorHeapBufferView(BufferView bufferView, in BufferViewCreateInfo createInfo)
    {
        if (bufferView.Handle == 0)
            return;

        ResourceRuntime.Descriptors.DescriptorHeapBufferViewCreateInfos[bufferView.Handle] = createInfo with { PNext = null };
        RegisterVulkanBufferViewResource(bufferView, createInfo.Buffer, "BufferView");
    }

    internal void UntrackDescriptorHeapBufferView(BufferView bufferView)
    {
        if (bufferView.Handle != 0)
            ResourceRuntime.Descriptors.DescriptorHeapBufferViewCreateInfos.TryRemove(bufferView.Handle, out _);
    }

    internal bool TryGetDescriptorHeapBufferViewCreateInfo(BufferView bufferView, out BufferViewCreateInfo createInfo)
    {
        if (bufferView.Handle != 0 &&
            ResourceRuntime.Descriptors.DescriptorHeapBufferViewCreateInfos.TryGetValue(bufferView.Handle, out createInfo))
        {
            return true;
        }

        createInfo = default;
        return false;
    }
}
