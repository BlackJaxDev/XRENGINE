namespace XREngine.Rendering.Compute;

/// <summary>
/// How the GPU BVH hierarchy is constructed.
/// </summary>
public enum BvhBuildMode
{
    /// <summary>Morton-code LBVH only.</summary>
    MortonOnly = 0,
    /// <summary>
    /// Reserved for a future topology-safe refinement implementation.
    /// Selecting this mode currently throws <see cref="System.NotSupportedException"/>.
    /// </summary>
    MortonPlusSah = 1
}
