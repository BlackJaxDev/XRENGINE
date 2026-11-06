namespace XREngine.Rendering.Models;

/// <summary>
/// Selects how an imported surface-detail texture (a normal, bump or height slot) is used.
/// The source slot often does not say: OBJ <c>map_bump</c> files can be height fields or
/// embossed bump maps.
/// </summary>
public enum ESurfaceDetailMapMode
{
    /// <summary>
    /// Classifies the texture content. Blue-dominant RGB is a normal map. Other colored maps are albedo (diffuse) textures. A grayscale map whose
    /// bright and dark details are balanced around its base value is a bump map. Other grayscale
    /// maps are height maps. An unreadable texture follows its source slot: a normal slot is a
    /// normal map and a bump or height slot is a bump map.
    /// </summary>
    Auto,

    /// <summary>
    /// Grayscale height field (white is the surface top). Drives parallax occlusion mapping and
    /// height-derived lighting normals.
    /// </summary>
    Height,

    /// <summary>
    /// Grayscale bump map. Drives height-derived lighting normals only, with no parallax.
    /// </summary>
    Bump,

    /// <summary>
    /// Tangent-space RGB normal map.
    /// </summary>
    Normal,

    /// <summary>
    /// Color (albedo, diffuse) texture filed in a detail slot. It is not used as surface detail. When the
    /// material has no base-color texture, it becomes the base color.
    /// </summary>
    Albedo,
}
