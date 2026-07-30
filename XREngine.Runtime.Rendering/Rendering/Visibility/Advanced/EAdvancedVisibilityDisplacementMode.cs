namespace XREngine.Rendering;

/// <summary>
/// Visibility treatment for materials whose geometry may modify final depth.
/// </summary>
public enum EAdvancedVisibilityDisplacementMode : uint
{
    None = 0u,
    VertexDepthAffecting = 1u,
    TessellatedDepthAffecting = 2u,
    UnsupportedFragmentDepth = 3u,
}
