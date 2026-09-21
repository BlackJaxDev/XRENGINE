using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private MaterialSurfaceTextureBinding[] _surfaceTextureBindings = [];
    private Vector3? _emissiveColor;
    private float? _emissionStrength;
    private float _transmission;
    private Vector3 _transmissionColor = Vector3.One;
    private float _normalScale = 1.0f;
    private static XRTexture2D? s_surfaceEmissionNeutralTexture;

    private static XRTexture2D SurfaceEmissionNeutralTexture
        => s_surfaceEmissionNeutralTexture ??= new XRTexture2D(1u, 1u, ColorF4.Black)
        {
            Name = "SurfaceEmissionNeutral",
            SamplerName = "SurfaceEmissionTexture",
        };

    /// <summary>Source-surface texture bindings, keyed by semantic rather than legacy sampler position.</summary>
    public MaterialSurfaceTextureBinding[] SurfaceTextureBindings
    {
        get => _surfaceTextureBindings;
        set
        {
            if (!SetField(ref _surfaceTextureBindings, value ?? []))
                return;

            IncrementBindingLayoutVersion();
            IncrementBindingValueVersion();
            IncrementBindingResourceVersion();
            EnsureSurfaceEmissionPublisher();
        }
    }

    /// <summary>Returns the first binding with the requested semantic without materializing a collection.</summary>
    public MaterialSurfaceTextureBinding? GetSurfaceTexture(EMaterialTextureSemantic semantic)
    {
        MaterialSurfaceTextureBinding[] bindings = SurfaceTextureBindings;
        for (int index = 0; index < bindings.Length; index++)
            if (bindings[index].Semantic == semantic)
                return bindings[index];

        return null;
    }

    /// <summary>Linear emissive tint authored by the source material, when it has one.</summary>
    public Vector3? EmissiveColor
    {
        get => _emissiveColor;
        set
        {
            if (SetField(ref _emissiveColor, value))
                IncrementBindingValueVersion();
            EnsureSurfaceEmissionPublisher();
        }
    }

    /// <summary>Source-material emissive strength, separate from the legacy scalar <c>Emission</c> uniform.</summary>
    public float? EmissionStrength
    {
        get => _emissionStrength;
        set
        {
            if (SetField(ref _emissionStrength, value))
                IncrementBindingValueVersion();
            EnsureSurfaceEmissionPublisher();
        }
    }

    /// <summary>Fraction of light transmitted through the surface.</summary>
    [DefaultValue(0.0f)]
    public float Transmission
    {
        get => _transmission;
        set
        {
            if (SetField(ref _transmission, value))
                IncrementBindingValueVersion();
        }
    }

    /// <summary>Linear tint applied to transmitted light.</summary>
    public Vector3 TransmissionColor
    {
        get => _transmissionColor;
        set
        {
            if (SetField(ref _transmissionColor, value))
                IncrementBindingValueVersion();
        }
    }

    /// <summary>Scale applied to source normal-map perturbation.</summary>
    [DefaultValue(1.0f)]
    public float NormalScale
    {
        get => _normalScale;
        set
        {
            if (SetField(ref _normalScale, value))
                IncrementBindingValueVersion();
        }
    }

    private bool _surfaceEmissionPublisherAttached;

    private void EnsureSurfaceEmissionPublisher()
    {
        if (_surfaceEmissionPublisherAttached)
            return;

        SettingUniforms += PublishSurfaceEmission;
        _surfaceEmissionPublisherAttached = true;
    }

    private void PublishSurfaceEmission(XRMaterialBase _, XRRenderProgram program)
    {
        MaterialSurfaceTextureBinding? emissive = GetSurfaceTexture(EMaterialTextureSemantic.Emissive);
        bool hasModernEmission = EmissiveColor.HasValue;
        bool sourceFormatDecodesSrgb = emissive?.Texture is XRTexture2D texture &&
            texture.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8;

        program.Uniform("SurfaceEmissionMode", hasModernEmission ? 1 : 0);
        program.Uniform("SurfaceEmissionColor", EmissiveColor ?? Vector3.One);
        program.Uniform("SurfaceEmissionStrength", EmissionStrength ?? Parameter<ShaderFloat>("Emission")?.Value ?? 0.0f);
        program.Uniform("SurfaceEmissionHasTexture", emissive is null ? 0 : 1);
        program.Uniform("SurfaceEmissionTextureIsSrgb", emissive is { IsSrgb: true } && !sourceFormatDecodesSrgb ? 1 : 0);
        program.Uniform("SurfaceEmissionTexCoordSet", emissive?.TexCoordSet ?? 0);
        program.Uniform("SurfaceEmissionUvScaleOffset", emissive?.UvScaleOffset ?? new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        program.Uniform("SurfaceEmissionUvRotation", emissive?.UvRotation ?? 0.0f);
        program.Sampler("SurfaceEmissionTexture", emissive?.Texture ?? SurfaceEmissionNeutralTexture, 9);
    }
}
