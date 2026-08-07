using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private void DestroyRemainingTrackedSamplers()
    {
        ulong[] handles = ResourceRuntime.Descriptors.TakeLiveSamplerHandles();
        if (handles.Length == 0)
            return;

        for (int i = 0; i < handles.Length; i++)
        {
            Sampler sampler = new() { Handle = handles[i] };
            Debug.Vulkan(
                "[Vulkan] Destroying remaining tracked sampler 0x{0:X} during renderer shutdown.",
                handles[i]);
            Api!.DestroySampler(_deviceContext.Device, sampler, null);
            CompleteVulkanResourceDestruction(ObjectType.Sampler, handles[i]);
        }

        Debug.Vulkan(
            "[Vulkan] Destroyed {0} remaining tracked sampler(s) during renderer shutdown.",
            handles.Length);
    }
}
