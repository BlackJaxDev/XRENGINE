namespace XREngine.Rendering.Models;

/// <summary>
/// Controls whether imported mesh decal materials use the Advanced surface decal lane.
/// </summary>
public enum EDecalMaterialMode
{
    /// <summary>
    /// A material with an opacity map and "decal" in its name is a surface decal. It keeps its
    /// masked fallback for renderers without the decal lane.
    /// </summary>
    Auto,
    /// <summary>No material is a surface decal. Opacity maps keep their masked or blended mode.</summary>
    Disabled,
}
