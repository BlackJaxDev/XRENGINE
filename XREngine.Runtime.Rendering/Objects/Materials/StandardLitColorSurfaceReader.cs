using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Shared value-type reader for the exact lit-color schema. A persistent reader caches
/// bindings; a temporary reader validates and samples without allocating a binding object.
/// </summary>
internal struct StandardLitColorSurfaceReader
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
    private const int AlphaCutoffBit = 1 << 10;
    private const int DeferredCommonBits = BaseColorBit | OpacityBit | SpecularBit | RoughnessBit | MetallicBit;
    private const int ForwardBits = MatColorBit | MatSpecularIntensityBit | MatShininessBit;

    private readonly XRMaterial _material;
    private readonly bool _authoredCooked;
    private ShaderVar[]? _parameters;
    private ShaderVar? _parameter0;
    private ShaderVar? _parameter1;
    private ShaderVar? _parameter2;
    private ShaderVar? _parameter3;
    private ShaderVar? _parameter4;
    private ShaderVar? _parameter5;
    private ShaderVar? _parameter6;
    private string? _name0;
    private string? _name1;
    private string? _name2;
    private string? _name3;
    private string? _name4;
    private string? _name5;
    private string? _name6;
    private ShaderVector3? _baseColor;
    private ShaderFloat? _opacity;
    private ShaderFloat? _specular;
    private ShaderFloat? _roughness;
    private ShaderFloat? _metallic;
    private ShaderFloat? _emission;
    private ShaderFloat? _indexOfRefraction;
    private ShaderVector4? _matColor;
    private ShaderFloat? _matSpecularIntensity;
    private ShaderFloat? _alphaCutoff;
    private StandardLitColorSurfaceSchema _schema;
    private ulong _layoutVersion;
    private ulong _valueVersion;
    private bool _hasLayout;
    private bool _hasSurface;
    private bool _unversionedLayout;
    private StandardLitColorSurface _surface;

    private bool HasCoverage => _material.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV2 ||
        _authoredCooked && _material.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2;

    public StandardLitColorSurfaceReader(XRMaterial material, bool authoredCooked = false)
    {
        _material = material;
        _authoredCooked = authoredCooked;
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

        ETransparencyMode transparency = _material.GetEffectiveTransparencyMode();
        int expectedPass = HasCoverage
            ? (int)(transparency switch
            {
                ETransparencyMode.Opaque => EDefaultRenderPass.OpaqueForward,
                ETransparencyMode.Masked => EDefaultRenderPass.MaskedForward,
                _ => EDefaultRenderPass.TransparentForward,
            })
            : _schema == StandardLitColorSurfaceSchema.Forward
                ? (int)EDefaultRenderPass.OpaqueForward
                : (int)EDefaultRenderPass.OpaqueDeferred;
        if (_material.RenderPass != expectedPass)
        {
            reason = "Standard lit-color parameter schema and coverage do not match the authored render pass.";
            return false;
        }

        // In-place edits to the public parameter array do not raise the base
        // material's value event for a replacement ShaderVar. Once observed,
        // read values on every call until the layout is republished normally.
        if (!_hasSurface || _authoredCooked || _unversionedLayout || _valueVersion != _material.BindingValueVersion)
        {
            _surface = _schema switch
            {
                StandardLitColorSurfaceSchema.Forward => ReadForward(),
                _ => ReadDeferred(),
            };
            _valueVersion = _material.BindingValueVersion;
            _hasSurface = true;
        }

        if (HasCoverage && (!float.IsFinite(_surface.Opacity) || _surface.Opacity is < 0 or > 1 ||
            _alphaCutoff is null || !float.IsFinite(_alphaCutoff.Value) || _alphaCutoff.Value is < 0 or > 1 ||
            _alphaCutoff.Value != _material.AlphaCutoff))
        {
            reason = "StandardLitColorV2 requires finite opacity/cutoff in [0,1] and a cutoff parameter matching AlphaCutoff.";
            return false;
        }
        if (_authoredCooked && (_schema != StandardLitColorSurfaceSchema.DeferredEmission || !HasCoverage && _surface.Opacity != 1 ||
            !float.IsFinite(_surface.BaseColor.X) || !float.IsFinite(_surface.BaseColor.Y) || !float.IsFinite(_surface.BaseColor.Z) ||
            !float.IsFinite(_surface.Specular) || !float.IsFinite(_surface.Roughness) ||
            !float.IsFinite(_surface.Metallic) || !float.IsFinite(_surface.Emission)))
        {
            reason = "Authored lit color requires finite PBR factors, with opacity one for the opaque V1 contract.";
            return false;
        }
        surface = HasCoverage
            ? _surface with { TransparencyMode = transparency, AlphaCutoff = _alphaCutoff!.Value }
            : _surface;
        reason = null;
        return true;
    }

    private bool ValidateMaterial(out string? reason)
    {
        if (_authoredCooked ? !_material.EngineSemantic.IsAuthoredLit() || _material.Shaders.Count == 0 :
            _material.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitColorV1 && !HasCoverage)
        {
            reason = "Material is not explicitly tagged with a supported StandardLitColor semantic.";
            return false;
        }

        if (_material.Textures.Count != 0 || _material.SurfaceTextureBindings.Length != 0 ||
            _material.EmissiveColor.HasValue || _material.EmissionStrength.HasValue ||
            _material.Transmission != 0.0f || _material.TransmissionColor != Vector3.One ||
            _material.NormalScale != 1.0f ||
            _material.HasSettingUniformsHandlers && !(_authoredCooked && _material.HasOnlyStandardSurfaceUniformHandlers) ||
            _material.HasSettingShadowUniformHandlers || _material.BindingPublishers.Count != 0)
        {
            reason = "StandardLitColorV1 has unsupported surface resources or binding extensions.";
            return false;
        }

        if (_authoredCooked && HasCoverage &&
            (_material.BillboardMode != EMeshBillboardMode.None || _material.HasSettingVertexUniformHandlers))
        {
            reason = "Authored color coverage does not admit billboard or custom vertex-uniform behavior.";
            return false;
        }

        if ((!HasCoverage && (_material.TransparencyMode != ETransparencyMode.Opaque ||
            _material.TransparentTechniqueOverride is not null || _material.AlphaCutoff != 0.5f)) ||
            (HasCoverage && !XRMaterial.IsStandardLitColorCoverageMode(_material.GetEffectiveTransparencyMode())) ||
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
            parameters.Length != (HasCoverage ? 7 : _schema == StandardLitColorSurfaceSchema.Forward ? 3 : 6) ||
            !ReferenceEquals(parameters[0], _parameter0) || parameters[0].Name != _name0 ||
            !ReferenceEquals(parameters[1], _parameter1) || parameters[1].Name != _name1 ||
            !ReferenceEquals(parameters[2], _parameter2) || parameters[2].Name != _name2)
            return false;

        return parameters.Length == 3 ||
            ReferenceEquals(parameters[3], _parameter3) && parameters[3].Name == _name3 &&
            ReferenceEquals(parameters[4], _parameter4) && parameters[4].Name == _name4 &&
            ReferenceEquals(parameters[5], _parameter5) && parameters[5].Name == _name5 &&
            (parameters.Length == 6 || ReferenceEquals(parameters[6], _parameter6) && parameters[6].Name == _name6);
    }

    private bool TryValidateLayout(ShaderVar[] parameters, out string? reason)
    {
        if (HasCoverage ? parameters.Length != 7 : parameters.Length is not (3 or 6))
        {
            reason = "StandardLitColorV1 requires six deferred or three forward parameters; V2 requires its seven coverage parameters.";
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
        ShaderFloat? alphaCutoff = null;

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
                "AlphaCutoff" when HasCoverage && parameter.GetType() == typeof(ShaderFloat) => AlphaCutoffBit,
                _ => 0,
            };

            if (bit == 0 || (mask & bit) != 0)
            {
                reason = "StandardLitColorV1 contains an unknown, duplicated, or incorrectly typed parameter.";
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
                case AlphaCutoffBit: alphaCutoff = (ShaderFloat)parameter!; break;
            }
        }

        StandardLitColorSurfaceSchema schema;
        if (HasCoverage && mask == (DeferredCommonBits | EmissionBit | AlphaCutoffBit))
            schema = StandardLitColorSurfaceSchema.DeferredEmission;
        else if (!HasCoverage && mask == (DeferredCommonBits | EmissionBit))
            schema = StandardLitColorSurfaceSchema.DeferredEmission;
        else if (!HasCoverage && mask == (DeferredCommonBits | IndexOfRefractionBit))
            schema = StandardLitColorSurfaceSchema.DeferredIndexOfRefraction;
        else if (!HasCoverage && mask == ForwardBits)
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
        _alphaCutoff = alphaCutoff;
        _parameters = parameters;
        _parameter0 = parameters[0]; _name0 = parameters[0].Name;
        _parameter1 = parameters[1]; _name1 = parameters[1].Name;
        _parameter2 = parameters[2]; _name2 = parameters[2].Name;
        _parameter3 = parameters.Length >= 6 ? parameters[3] : null;
        _name3 = _parameter3?.Name;
        _parameter4 = parameters.Length >= 6 ? parameters[4] : null;
        _name4 = _parameter4?.Name;
        _parameter5 = parameters.Length >= 6 ? parameters[5] : null;
        _name5 = _parameter5?.Name;
        _parameter6 = parameters.Length == 7 ? parameters[6] : null;
        _name6 = _parameter6?.Name;
        _layoutVersion = _material.BindingLayoutVersion;
        _valueVersion = 0;
        _hasSurface = false;
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
