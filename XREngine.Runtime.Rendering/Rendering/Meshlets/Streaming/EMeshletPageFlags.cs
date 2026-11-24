namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Flags in the header of one geometry page (see <see cref="MeshletPageHeader"/>).
/// </summary>
[Flags]
public enum EMeshletPageFlags : ushort
{
    /// <summary>A static page: its vertex indices refer to the page-local vertices that follow them.</summary>
    None = 0,
    /// <summary>
    /// A skinned topology page: its vertex indices refer to source vertices, and the page holds no
    /// vertices. The deformation output supplies the vertex data.
    /// </summary>
    SkinnedTopology = 1 << 0,
    /// <summary>
    /// A root page: it holds only root groups (groups that the cook could not simplify further).
    /// A root page has no dependencies and stays resident while its geometry is registered.
    /// </summary>
    Root = 1 << 1,
}
