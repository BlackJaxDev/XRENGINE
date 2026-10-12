using XREngine.Rendering.Materials;

namespace XREngine.Rendering;

/// <summary>The exact opaque deferred texture inputs shared by desktop authoring and cooked rasterization.</summary>
public readonly record struct StandardLitTextureSurface(
    StandardLitColorSurface Values,
    MaterialSurfaceTextureBinding BaseColor,
    MaterialSurfaceTextureBinding? Normal,
    MaterialSurfaceTextureBinding? Metallic,
    MaterialSurfaceTextureBinding? Roughness)
{
    public string VertexProfile => Normal is null ? "position-normal-uv-v1" : "position-normal-tangent-uv-v1";
}
