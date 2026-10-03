using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private XRMaterial? _standardLitColorSourceMaterial;
    private EStandardLitColorAuxiliaryPass _standardLitColorAuxiliaryPass;
    private XRMaterial? _standardLitSpotShadowVariant;

    /// <summary>Owns the projected color-depth replay separately from depth-only replay.</summary>
    internal XRMaterial GetStandardLitSpotShadowVariant()
    {
        if (_standardLitSpotShadowVariant is not null) return _standardLitSpotShadowVariant;
        XRMaterial variant = Rendering.Shaders.StandardLitColorVariantFactory.Create(this, EStandardLitColorAuxiliaryPass.SpotShadowDepth)
            ?? throw new NotSupportedException("StandardLitColor.SpotShadowUnsupported: an exact V2 coverage surface is required.");
        SetField(ref _standardLitSpotShadowVariant, variant, publishNotifications: false);
        return variant;
    }

    private void DestroyStandardLitSpotShadowVariant(bool now = false)
    {
        _standardLitSpotShadowVariant?.Destroy(now);
        SetField(ref _standardLitSpotShadowVariant, null, publishNotifications: false);
    }

    /// <summary>The live authored surface behind a lazily owned auxiliary material.</summary>
    [Browsable(false), YamlIgnore]
    public XRMaterial? StandardLitColorSourceMaterial
    {
        get => _standardLitColorSourceMaterial;
        internal set => SetField(ref _standardLitColorSourceMaterial, value);
    }

    /// <summary>Identifies an auxiliary output without changing the source material's pass.</summary>
    [Browsable(false), YamlIgnore]
    public EStandardLitColorAuxiliaryPass StandardLitColorAuxiliaryPass
    {
        get => _standardLitColorAuxiliaryPass;
        internal set => SetField(ref _standardLitColorAuxiliaryPass, value);
    }

    /// <summary>
    /// Creates an explicitly versioned lit-color surface with uniform alpha coverage.
    /// Masked surfaces discard opacity below the cutoff in color, normal and shadow
    /// passes. Sorted modes retain the engine's authored blending and sort priorities.
    /// Existing lit-color factories retain their original opaque semantic.
    /// </summary>
    public static XRMaterial CreateLitColorCoverageMaterial(ColorF4 color,
        ETransparencyMode transparencyMode = ETransparencyMode.AlphaBlend, float alphaCutoff = 0.5f)
    {
        if (!IsStandardLitColorCoverageMode(transparencyMode))
            throw new ArgumentOutOfRangeException(nameof(transparencyMode), transparencyMode, "Unsupported lit-color coverage mode.");
        if (!float.IsFinite(color.A) || color.A is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(color), "Opacity must be finite and between zero and one.");
        if (!float.IsFinite(alphaCutoff) || alphaCutoff is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(alphaCutoff));

        ShaderVar[] parameters =
        [
            new ShaderVector3((ColorF3)color, "BaseColor"),
            new ShaderFloat(color.A, "Opacity"),
            new ShaderFloat(1.0f, "Specular"),
            new ShaderFloat(0.5f, "Roughness"),
            new ShaderFloat(0.0f, "Metallic"),
            new ShaderFloat(0.0f, "Emission"),
            new ShaderFloat(alphaCutoff, "AlphaCutoff"),
        ];
        XRMaterial material = RuntimeEngineMaterialConstructionServices.Target switch
        {
            EngineMaterialConstructionTarget.DesktopGlsl => new(parameters,
                ShaderHelper.LoadEngineShader("Common/StandardLitColorCoverageForward.fs")),
            EngineMaterialConstructionTarget.WebGpuCooked => new(parameters),
            _ => throw new InvalidOperationException("Unsupported built-in material construction target."),
        };
        // The factory owns this exact schema; shader/snippet discovery may expose
        // additional engine-managed uniforms while constructing the desktop stages.
        material.Parameters = parameters;
        material.AlphaCutoff = alphaCutoff;
        material.TransparencyMode = transparencyMode;
        material.ApplyTransparencyState();
        material.EngineSemantic = EngineMaterialSemanticIdentity.StandardLitColorV2;
        return material;
    }

    /// <summary>Modes with an exact uniform-alpha color/depth/shadow contract.</summary>
    public static bool IsStandardLitColorCoverageMode(ETransparencyMode mode)
        => mode is ETransparencyMode.Opaque or ETransparencyMode.Masked or ETransparencyMode.AlphaBlend
            or ETransparencyMode.PremultipliedAlpha or ETransparencyMode.Additive;

    internal void PublishStandardLitColorCoverage(XRRenderProgram program)
    {
        XRMaterial source = StandardLitColorSourceMaterial ?? ShadowBindingSourceMaterial ?? this;
        if (!source.EngineSemantic.IsColorCoverage())
            return;
        ETransparencyMode mode = source.GetEffectiveTransparencyMode();
        if (!IsStandardLitColorCoverageMode(mode))
            throw new NotSupportedException("StandardLitColor.CoverageUnsupported: the authored mode requires a different surface contract.");
        if ((StandardLitColorAuxiliaryPass != EStandardLitColorAuxiliaryPass.None || RuntimeEngine.Rendering.State.IsShadowPass) &&
            mode is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
            throw new NotSupportedException("StandardLitColor.AuxiliaryCoverageUnsupported: sorted surfaces cannot enter depth or shadow replay.");
        program.Uniform("StandardLitCoverage", new Vector4(mode == ETransparencyMode.Masked ? 1 : 0,
            source.AlphaCutoff, mode == ETransparencyMode.PremultipliedAlpha ? 1 : 0, 0));
    }
}
