using System;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanDynamicRenderingUtilities
{
    public static uint ResolveLayerCount(uint framebufferLayers, uint viewMask)
        => viewMask == 0u ? Math.Max(framebufferLayers, 1u) : 1u;
}
