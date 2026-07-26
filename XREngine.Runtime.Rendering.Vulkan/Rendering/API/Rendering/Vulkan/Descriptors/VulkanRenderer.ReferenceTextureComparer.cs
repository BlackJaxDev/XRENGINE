using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Compares XRTexture instances by reference, providing a singleton instance for use in collections.
    /// </summary>
    private sealed class ReferenceTextureComparer : IEqualityComparer<XRTexture>
    {
        public static readonly ReferenceTextureComparer Instance = new();

        public bool Equals(XRTexture? x, XRTexture? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(XRTexture obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
