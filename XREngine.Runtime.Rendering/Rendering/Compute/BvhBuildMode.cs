namespace XREngine.Rendering.Compute;

/// <summary>
/// How the GPU BVH hierarchy is constructed.
/// </summary>
public enum BvhBuildMode
{
    /// <summary>Morton-code LBVH only.</summary>
    MortonOnly = 0,
    /// <summary>Morton-code LBVH followed by SAH refinement pass.</summary>
    MortonPlusSah = 1
}
