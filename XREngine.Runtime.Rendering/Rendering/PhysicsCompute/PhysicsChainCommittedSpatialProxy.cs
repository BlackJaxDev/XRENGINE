using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Sizes the committed CPU bound of a covered renderer. The committed bound is an enlarged proxy of
/// the exact spatial bound. While the exact bound stays inside the proxy, the proxy and its version
/// stay the same, so CPU spatial structures do not move the renderer. The margin trades CPU culling
/// precision against move frequency. The proxy always contains the exact bound, so culling stays
/// conservative.
/// </summary>
internal static class PhysicsChainCommittedSpatialProxy
{
    /// <summary>Each side of a new proxy extends the exact bound by this fraction of its largest extent.</summary>
    public const float MarginFraction = 0.25f;

    /// <summary>
    /// A proxy is fitted again when its largest extent exceeds this multiple of the exact bound's
    /// largest extent. A new proxy is 1.5 times the exact extent, so the exact bound can shrink by
    /// a quarter before the proxy shrinks with it.
    /// </summary>
    public const float MaximumLooseness = 2.0f;

    /// <summary>Fits a new proxy around <paramref name="exact"/>.</summary>
    public static AABB Create(in AABB exact)
    {
        Vector3 margin = new(LargestExtent(exact) * MarginFraction);
        return new(exact.Min - margin, exact.Max + margin);
    }

    /// <summary>Returns true when <paramref name="proxy"/> contains <paramref name="exact"/> and is not too loose for it.</summary>
    public static bool CanKeep(in AABB proxy, in AABB exact)
        => Vector3.Min(proxy.Min, exact.Min) == proxy.Min
            && Vector3.Max(proxy.Max, exact.Max) == proxy.Max
            && LargestExtent(proxy) <= LargestExtent(exact) * MaximumLooseness;

    private static float LargestExtent(in AABB bounds)
    {
        Vector3 size = bounds.Max - bounds.Min;
        return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
    }
}
