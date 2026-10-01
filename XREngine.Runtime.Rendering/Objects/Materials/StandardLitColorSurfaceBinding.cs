using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Validates an engine-authored lit-color material and reads its numeric surface
/// without building a backend-specific material or allocating during steady-state reads.
/// A binding is owned by one render consumer; it is not a cross-thread snapshot.
/// </summary>
public sealed class StandardLitColorSurfaceBinding
{
    private const int BaseColorBit = 1 << 0;
    private const int OpacityBit = 1 << 1;
    private const int SpecularBit = 1 << 2;
    private const int RoughnessBit = 1 << 3;
    private const int MetallicBit = 1 << 4;
    private const int EmissionBit = 1 << 5;
    private const int IndexOfRefractionBit = 1 << 6;
    private const int MatColorBit = 1 << 7;
    private const int MatSpecularIntensityBit = 1 << 8;
    private const int MatShininessBit = 1 << 9;
    private const int DeferredCommonBits = BaseColorBit | OpacityBit | SpecularBit | RoughnessBit | MetallicBit;
    private const int ForwardBits = MatColorBit | MatSpecularIntensityBit | MatShininessBit;

    private readonly XRMaterial _material;
    private ShaderVar[]? _parameters;
    private ShaderVar? _parameter0;
    private ShaderVar? _parameter1;
    private ShaderVar? _parameter2;
    private ShaderVar? _parameter3;
    private ShaderVar? _parameter4;
    private ShaderVar? _parameter5;
    private string? _name0;
    private string? _name1;
    private string? _name2;
    private string? _name3;
    private string? _name4;
    private string? _name5;
    private ShaderVector3? _baseColor;
    private ShaderFloat? _opacity;
    private ShaderFloat? _specular;
    private ShaderFloat? _roughness;
    private ShaderFloat? _metallic;
    private ShaderFloat? _emission;
    private ShaderFloat? _indexOfRefraction;
    private ShaderVector4? _matColor;
    private ShaderFloat? _matSpecularIntensity;
    private StandardLitColorSurfaceSchema _schema;
    private ulong _layoutVersion;
    private ulong _valueVersion;
    private bool _hasLayout;
    private bool _unversionedLayout;
    private StandardLitColorSurface _surface;

    private StandardLitColorSurfaceBinding(XRMaterial material)
        => _material = material;

    /// <summary>
    /// Admits only an explicitly tagged material with one complete, unmixed factory
    /// parameter layout. A failed admission never silently chooses an alternate shader.
    /// </summary>
    public static bool TryCreate(XRMaterial material, out StandardLitColorSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);

        StandardLitColorSurfaceBinding candidate = new(material);
        if (!candidate.TryRead(out _, out reason))
        {
            binding = null;
            return false;
        }

        binding = candidate;
        return true;
    }

    /// <summary>
    /// Reads current numeric values. BindingLayoutVersion normally gates schema
    /// validation; direct edits to the exposed parameter array or parameter names
    /// are detected by reference/name checks and validated again.
    /// </summary>
    public bool TryRead(out StandardLitColorSurface surface, out string? reason)
    {
        surface = default;
        if (!ValidateMaterial(out reason))
            return false;

        ShaderVar[] parameters = _material.Parameters;
        ulong layoutVersion = _material.BindingLayoutVersion;
        bool layoutVersionChanged = _layoutVersion != layoutVersion;
        bool parametersChanged = _hasLayout && !MatchesCachedParameters(parameters);
        if (!layoutVersionChanged && parametersChanged)
            _unversionedLayout = true;
        if (!_hasLayout || layoutVersionChanged || parametersChanged)
        {
            if (!TryValidateLayout(parameters, out reason))
            {
                _hasLayout = false;
                return false;
            }
            if (layoutVersionChanged)
                _unversionedLayout = false;
        }

        int expectedPass = _schema == StandardLitColorSurfaceSchema.Forward
            ? (int)EDefaultRenderPass.OpaqueForward
            : (int)EDefaultRenderPass.OpaqueDeferred;
        if (_material.RenderPass != expectedPass)
        {
            reason = "StandardLitColorV1 parameter schema does not match its opaque render pass.";
            return false;
        }

        // In-place edits to the public parameter array do not raise the base
        // material's value event for a replacement ShaderVar. Once observed,
        // read values on every call until the layout is republished normally.
        if (_unversionedLayout || _valueVersion != _material.BindingValueVersion)
        {
            _surface = _schema switch
            {
                StandardLitColorSurfaceSchema.Forward => ReadForward(),
                _ => ReadDeferred(),
            };
            _valueVersion = _material.BindingValueVersion;
        }

        surface = _surface;
        reason = null;
        return true;
    }

    private bool ValidateMaterial(out string? reason)
    {
        if (_material.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitColorV1)
        {
            reason = "Material is not explicitly tagged StandardLitColorV1.";
            return false;
        }

        if (_material.Textures.Count != 0 || _material.SurfaceTextureBindings.Length != 0 ||
            _material.EmissiveColor.HasValue || _material.EmissionStrength.HasValue ||
            _material.Transmission != 0.0f || _material.TransmissionColor != Vector3.One ||
            _material.NormalScale != 1.0f || _material.HasSettingUniformsHandlers ||
            _material.HasSettingShadowUniformHandlers || _material.BindingPublishers.Count != 0)
        {
            reason = "StandardLitColorV1 has unsupported surface resources or binding extensions.";
            return false;
        }

        if (_material.TransparencyMode != ETransparencyMode.Opaque ||
            _material.TransparentTechniqueOverride is not null ||
            _material.AlphaCutoff != 0.5f ||
            _material.PassSet.Passes.Length != 0 ||
            _material.PassSet.DisabledSourcePasses.Length != 0 ||
            _material.PassSet.SourceRenderQueue != -1 ||
            _material.PassSet.QueuePriority != 0 ||
            _material.PassSet.ForwardAddRenderOptions is not null ||
            _material.PassSet.ForwardAddPolicy != EMaterialForwardAddPolicy.FoldedIntoForwardPlusBase ||
            _material.UberAuthoredState.Features.Length != 0 ||
            _material.UberAuthoredState.Properties.Length != 0 ||
            !_material.RequestedUberVariant.IsEmpty)
        {
            reason = "StandardLitColorV1 has unsupported pass, transparency, or uber extensions.";
            return false;
        }

        reason = null;
        return true;
    }

    private bool MatchesCachedParameters(ShaderVar[] parameters)
    {
        if (!ReferenceEquals(parameters, _parameters) ||
            parameters.Length != (_schema == StandardLitColorSurfaceSchema.Forward ? 3 : 6) ||
            !ReferenceEquals(parameters[0], _parameter0) || parameters[0].Name != _name0 ||
            !ReferenceEquals(parameters[1], _parameter1) || parameters[1].Name != _name1 ||
            !ReferenceEquals(parameters[2], _parameter2) || parameters[2].Name != _name2)
            return false;

        return parameters.Length == 3 ||
            ReferenceEquals(parameters[3], _parameter3) && parameters[3].Name == _name3 &&
            ReferenceEquals(parameters[4], _parameter4) && parameters[4].Name == _name4 &&
            ReferenceEquals(parameters[5], _parameter5) && parameters[5].Name == _name5;
    }

    private bool TryValidateLayout(ShaderVar[] parameters, out string? reason)
    {
        if (parameters.Length is not (3 or 6))
        {
            reason = "StandardLitColorV1 requires exactly six deferred or three forward parameters.";
            return false;
        }

        int mask = 0;
        ShaderVector3? baseColor = null;
        ShaderFloat? opacity = null;
        ShaderFloat? specular = null;
        ShaderFloat? roughness = null;
        ShaderFloat? metallic = null;
        ShaderFloat? emission = null;
        ShaderFloat? indexOfRefraction = null;
        ShaderVector4? matColor = null;
        ShaderFloat? matSpecularIntensity = null;

        for (int index = 0; index < parameters.Length; index++)
        {
            ShaderVar? parameter = parameters[index];
            int bit = parameter?.Name switch
            {
                "BaseColor" when parameter.GetType() == typeof(ShaderVector3) => BaseColorBit,
                "Opacity" when parameter.GetType() == typeof(ShaderFloat) => OpacityBit,
                "Specular" when parameter.GetType() == typeof(ShaderFloat) => SpecularBit,
                "Roughness" when parameter.GetType() == typeof(ShaderFloat) => RoughnessBit,
                "Metallic" when parameter.GetType() == typeof(ShaderFloat) => MetallicBit,
                "Emission" when parameter.GetType() == typeof(ShaderFloat) => EmissionBit,
                "IndexOfRefraction" when parameter.GetType() == typeof(ShaderFloat) => IndexOfRefractionBit,
                "MatColor" when parameter.GetType() == typeof(ShaderVector4) => MatColorBit,
                "MatSpecularIntensity" when parameter.GetType() == typeof(ShaderFloat) => MatSpecularIntensityBit,
                "MatShininess" when parameter.GetType() == typeof(ShaderFloat) => MatShininessBit,
                _ => 0,
            };

            if (bit == 0 || (mask & bit) != 0)
            {
                reason = $"StandardLitColorV1 parameter '{parameter?.Name ?? "<null>"}' is unknown, duplicated, or has the wrong type.";
                return false;
            }

            mask |= bit;
            switch (bit)
            {
                case BaseColorBit: baseColor = (ShaderVector3)parameter!; break;
                case OpacityBit: opacity = (ShaderFloat)parameter!; break;
                case SpecularBit: specular = (ShaderFloat)parameter!; break;
                case RoughnessBit: roughness = (ShaderFloat)parameter!; break;
                case MetallicBit: metallic = (ShaderFloat)parameter!; break;
                case EmissionBit: emission = (ShaderFloat)parameter!; break;
                case IndexOfRefractionBit: indexOfRefraction = (ShaderFloat)parameter!; break;
                case MatColorBit: matColor = (ShaderVector4)parameter!; break;
                case MatSpecularIntensityBit: matSpecularIntensity = (ShaderFloat)parameter!; break;
            }
        }

        StandardLitColorSurfaceSchema schema;
        if (mask == (DeferredCommonBits | EmissionBit))
            schema = StandardLitColorSurfaceSchema.DeferredEmission;
        else if (mask == (DeferredCommonBits | IndexOfRefractionBit))
            schema = StandardLitColorSurfaceSchema.DeferredIndexOfRefraction;
        else if (mask == ForwardBits)
            schema = StandardLitColorSurfaceSchema.Forward;
        else
        {
            reason = "StandardLitColorV1 parameters do not form one supported factory schema.";
            return false;
        }

        _schema = schema;
        _baseColor = baseColor;
        _opacity = opacity;
        _specular = specular;
        _roughness = roughness;
        _metallic = metallic;
        _emission = emission;
        _indexOfRefraction = indexOfRefraction;
        _matColor = matColor;
        _matSpecularIntensity = matSpecularIntensity;
        _parameters = parameters;
        _parameter0 = parameters[0]; _name0 = parameters[0].Name;
        _parameter1 = parameters[1]; _name1 = parameters[1].Name;
        _parameter2 = parameters[2]; _name2 = parameters[2].Name;
        _parameter3 = parameters.Length == 6 ? parameters[3] : null;
        _name3 = _parameter3?.Name;
        _parameter4 = parameters.Length == 6 ? parameters[4] : null;
        _name4 = _parameter4?.Name;
        _parameter5 = parameters.Length == 6 ? parameters[5] : null;
        _name5 = _parameter5?.Name;
        _layoutVersion = _material.BindingLayoutVersion;
        _valueVersion = 0;
        _hasLayout = true;
        reason = null;
        return true;
    }

    private StandardLitColorSurface ReadDeferred()
        => new(
            _schema,
            _baseColor!.Value,
            _opacity!.Value,
            _specular!.Value,
            _roughness!.Value,
            _metallic!.Value,
            _emission?.Value ?? 0.0f,
            _indexOfRefraction?.Value ?? 1.0f);

    private StandardLitColorSurface ReadForward()
    {
        Vector4 color = _matColor!.Value;
        // ForwardLighting.glsl declares these uniforms with these defaults.
        // MatShininess is declared by LitColoredForward.fs but is not consumed.
        return new(
            StandardLitColorSurfaceSchema.Forward,
            new Vector3(color.X, color.Y, color.Z),
            color.W,
            _matSpecularIntensity!.Value,
            0.9f,
            0.0f,
            0.0f,
            1.0f);
    }
}
