namespace XREngine.Rendering.UI;

/// <summary>
/// A vector icon that the batched UI quad shader draws in place of the box. The shader computes the icon
/// shape from line segments in the quad, so the icon stays sharp at every size and needs no font glyph.
/// The quad fill color is the icon color. A border width above zero sets the stroke width in pixels.
/// </summary>
public enum EUIQuadIcon : uint
{
    /// <summary>No icon: the quad draws its box.</summary>
    None = 0u,
    /// <summary>A chevron that points right, for a collapsed tree item or a link to a detail view.</summary>
    ChevronRight = 1u,
    /// <summary>A chevron that points down, for an expanded tree item or section.</summary>
    ChevronDown = 2u,
    /// <summary>A check mark, for a checked box.</summary>
    Check = 3u,
    /// <summary>A horizontal dash, for a box with mixed values.</summary>
    Minus = 4u,
    /// <summary>A diagonal cross, for close and remove buttons.</summary>
    Cross = 5u,
    /// <summary>A magnifier (a ring with a handle), for search fields.</summary>
    Search = 6u,
}
