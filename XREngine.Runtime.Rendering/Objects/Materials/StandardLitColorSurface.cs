using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Numeric inputs of an explicitly tagged engine lit-color material. These are raw
/// authored values; the renderer remains responsible for its lighting equations.
/// </summary>
public readonly record struct StandardLitColorSurface(
    StandardLitColorSurfaceSchema Schema,
    Vector3 BaseColor,
    float Opacity,
    float Specular,
    float Roughness,
    float Metallic,
    float Emission,
    float IndexOfRefraction);
