using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Scene.Components.Landscape;

/// <summary>
/// Defines a single terrain material layer with textures and blending properties.
/// </summary>
public class TerrainLayer : XRBase
{
    private string _name = "Layer";
    private XRTexture2D? _diffuseTexture;
    private XRTexture2D? _normalTexture;
    private XRTexture2D? _heightTexture;
    private XRTexture2D? _roughnessTexture;
    private XRTexture2D? _metallicTexture;
    private XRTexture2D? _aoTexture;
    private Vector2 _tiling = Vector2.One;
    private Vector2 _offset = Vector2.Zero;
    private float _normalStrength = 1.0f;
    private float _heightStrength = 0.05f;
    private ColorF4 _tint = ColorF4.White;
    private float _roughness = 0.5f;
    private float _metallic = 0.0f;

    [Description("Name identifier for this layer.")]
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    [Description("Diffuse/albedo texture for this layer.")]
    public XRTexture2D? DiffuseTexture
    {
        get => _diffuseTexture;
        set => SetField(ref _diffuseTexture, value);
    }

    [Description("Normal map texture for this layer.")]
    public XRTexture2D? NormalTexture
    {
        get => _normalTexture;
        set => SetField(ref _normalTexture, value);
    }

    [Description("Height/displacement map for this layer.")]
    public XRTexture2D? HeightTexture
    {
        get => _heightTexture;
        set => SetField(ref _heightTexture, value);
    }

    [Description("Roughness map for this layer.")]
    public XRTexture2D? RoughnessTexture
    {
        get => _roughnessTexture;
        set => SetField(ref _roughnessTexture, value);
    }

    [Description("Metallic map for this layer.")]
    public XRTexture2D? MetallicTexture
    {
        get => _metallicTexture;
        set => SetField(ref _metallicTexture, value);
    }

    [Description("Ambient occlusion map for this layer.")]
    public XRTexture2D? AOTexture
    {
        get => _aoTexture;
        set => SetField(ref _aoTexture, value);
    }

    [Description("UV tiling scale for this layer.")]
    public Vector2 Tiling
    {
        get => _tiling;
        set => SetField(ref _tiling, value);
    }

    [Description("UV offset for this layer.")]
    public Vector2 Offset
    {
        get => _offset;
        set => SetField(ref _offset, value);
    }

    [Description("Normal map strength multiplier.")]
    public float NormalStrength
    {
        get => _normalStrength;
        set => SetField(ref _normalStrength, value);
    }

    [Description("Parallax/height mapping strength.")]
    public float HeightStrength
    {
        get => _heightStrength;
        set => SetField(ref _heightStrength, value);
    }

    [Description("Base color tint for this layer.")]
    public ColorF4 Tint
    {
        get => _tint;
        set => SetField(ref _tint, value);
    }

    [Description("Roughness value when no texture is provided.")]
    public float Roughness
    {
        get => _roughness;
        set => SetField(ref _roughness, value);
    }

    [Description("Metallic value when no texture is provided.")]
    public float Metallic
    {
        get => _metallic;
        set => SetField(ref _metallic, value);
    }
}
