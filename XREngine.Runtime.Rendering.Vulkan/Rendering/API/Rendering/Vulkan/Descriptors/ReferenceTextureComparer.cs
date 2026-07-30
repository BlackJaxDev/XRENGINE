using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compares texture objects by identity for renderer-local descriptor tables.
/// </summary>
internal sealed class ReferenceTextureComparer : IEqualityComparer<XRTexture>
{
    internal static readonly ReferenceTextureComparer Instance = new();

    public bool Equals(XRTexture? x, XRTexture? y)
        => ReferenceEquals(x, y);

    public int GetHashCode(XRTexture obj)
        => RuntimeHelpers.GetHashCode(obj);
}
