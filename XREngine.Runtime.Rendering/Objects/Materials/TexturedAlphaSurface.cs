using XREngine.Rendering.Materials;

namespace XREngine.Rendering;

/// <summary>The exact two-image forward surface; shininess is retained but unused by the canonical PBR shader.</summary>
public readonly record struct TexturedAlphaSurface(
    StandardLitColorSurface Values,
    MaterialSurfaceTextureBinding BaseColor,
    MaterialSurfaceTextureBinding Opacity,
    float MatShininess)
{
    public string VertexProfile => "position-normal-uv-v1";
}
