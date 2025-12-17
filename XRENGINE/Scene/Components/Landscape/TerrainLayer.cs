using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering;

namespace XREngine.Scene.Components.Landscape;

/// <summary>
/// Defines a single terrain material layer with textures and blending properties.
/// </summary>
public class TerrainLayer
{
    [Description("Name identifier for this layer.")]
    public string Name { get; set; } = "Layer";

    [Description("Diffuse/albedo texture for this layer.")]
    public XRTexture2D? DiffuseTexture { get; set; }

    [Description("Normal map texture for this layer.")]
    public XRTexture2D? NormalTexture { get; set; }

    [Description("Height/displacement map for this layer.")]
    public XRTexture2D? HeightTexture { get; set; }

    [Description("Roughness map for this layer.")]
    public XRTexture2D? RoughnessTexture { get; set; }

    [Description("Metallic map for this layer.")]
    public XRTexture2D? MetallicTexture { get; set; }

    [Description("Ambient occlusion map for this layer.")]
    public XRTexture2D? AOTexture { get; set; }

    [Description("UV tiling scale for this layer.")]
    public Vector2 Tiling { get; set; } = Vector2.One;

    [Description("UV offset for this layer.")]
    public Vector2 Offset { get; set; } = Vector2.Zero;

    [Description("Normal map strength multiplier.")]
    public float NormalStrength { get; set; } = 1.0f;

    [Description("Parallax/height mapping strength.")]
    public float HeightStrength { get; set; } = 0.05f;

    [Description("Base color tint for this layer.")]
    public ColorF4 Tint { get; set; } = ColorF4.White;

    [Description("Roughness value when no texture is provided.")]
    public float Roughness { get; set; } = 0.5f;

    [Description("Metallic value when no texture is provided.")]
    public float Metallic { get; set; } = 0.0f;
}
