namespace XREngine.Rendering.Commands;

/// <summary>
/// Bits of <see cref="AdvancedGeometryRecord.Flags"/>. Shaders test the same values
/// (<c>XR_ADV_GEOMETRY_FLAG_*</c> in <c>VisibilityStreamedGeometry.glslinc</c>).
/// </summary>
[Flags]
public enum EAdvancedGeometryFlags : uint
{
    None = 0u,

    /// <summary>
    /// The cluster hierarchy geometry draws from geometry pages. Its triangle-word stream holds the
    /// page block (see <see cref="AdvancedPublishedMeshletLayout.Pages"/>), and the meshlet task and
    /// mesh stages and the surface reconstruction read the descriptors, meshlet vertex indices,
    /// triangle bytes and static vertices of its clusters from the page images.
    /// </summary>
    StreamedPages = 1u << 0,
}
