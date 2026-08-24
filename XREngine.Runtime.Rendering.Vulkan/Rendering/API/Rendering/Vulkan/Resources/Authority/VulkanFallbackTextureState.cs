using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns the canonical fallback texture generation for one resource runtime.</summary>
internal sealed class VulkanFallbackTextureState
{
    internal Image Image;
    internal DeviceMemory Memory;
    internal ImageView View;
    internal ImageView View2DArray;
    internal ImageView ViewCube;
    internal ImageView ViewCubeArray;
    internal Sampler Sampler;
    internal bool Ready;
}
