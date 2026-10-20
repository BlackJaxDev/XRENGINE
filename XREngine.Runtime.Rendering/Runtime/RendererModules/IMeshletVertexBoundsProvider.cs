namespace XREngine.Rendering;

/// <summary>
/// Optional authored vertex-bounds declaration on a material or renderer binding publisher.
/// An undeclared program receives conservative unbounded visibility while still using GPU meshlet expansion.
/// </summary>
public interface IMeshletVertexBoundsProvider
{
    /// <summary>Disables geometric rejection when authored vertex work has no finite conservative local bound.</summary>
    bool DisableMeshletCulling { get; }

    /// <summary>
    /// Nonnegative radius added after canonical skin/morph deformation, in the frozen resident model's local coordinates.
    /// The declaration must cover every authored position override, callback, binding publisher and vertex displacement used by the draw.
    /// </summary>
    float MeshletBoundsExpansion { get; }
}
