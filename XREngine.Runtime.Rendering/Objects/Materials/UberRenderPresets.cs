using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Applies and detects Uber render presets. The editor and the material importers use the same table, so an
/// imported material and an authored material with the same preset have the same render state.
/// </summary>
public static class UberRenderPresets
{
    // These values match the BLEND_MODE_* defines in Uber/common.glsl.
    private const int ShaderModeOpaque = 0;
    private const int ShaderModeCutout = 1;
    private const int ShaderModeFade = 2;
    private const int ShaderModeTransparent = 3;
    private const int ShaderModeAdditive = 4;
    private const int ShaderModeTransparentCutout = 5;

    private static readonly UberRenderPresetDefinition[] Definitions =
    [
        new(EUberRenderPreset.Opaque, "Opaque", ETransparencyMode.Opaque, ShaderModeOpaque,
            false, EBlendingFactor.One, EBlendingFactor.Zero, true, 0.0f, false),
        new(EUberRenderPreset.Cutout, "Cutout", ETransparencyMode.Masked, ShaderModeCutout,
            false, EBlendingFactor.One, EBlendingFactor.Zero, true, 0.5f, false),
        new(EUberRenderPreset.TransparentCutout, "Transparent Cutout", ETransparencyMode.Masked, ShaderModeTransparentCutout,
            true, EBlendingFactor.SrcAlpha, EBlendingFactor.OneMinusSrcAlpha, true, 0.01f, false),
        new(EUberRenderPreset.Fade, "Fade", ETransparencyMode.AlphaBlend, ShaderModeFade,
            true, EBlendingFactor.SrcAlpha, EBlendingFactor.OneMinusSrcAlpha, false, 0.002f, false),
        new(EUberRenderPreset.Transparent, "Transparent", ETransparencyMode.PremultipliedAlpha, ShaderModeTransparent,
            true, EBlendingFactor.One, EBlendingFactor.OneMinusSrcAlpha, false, 0.0f, true),
        new(EUberRenderPreset.Additive, "Additive", ETransparencyMode.Additive, ShaderModeAdditive,
            true, EBlendingFactor.One, EBlendingFactor.One, false, 0.0f, false),
        new(EUberRenderPreset.SoftAdditive, "Soft Additive", ETransparencyMode.AlphaBlend, ShaderModeTransparent,
            true, EBlendingFactor.OneMinusDstColor, EBlendingFactor.One, false, 0.0f, false),
        new(EUberRenderPreset.Multiply, "Multiply", ETransparencyMode.AlphaBlend, ShaderModeTransparent,
            true, EBlendingFactor.DstColor, EBlendingFactor.Zero, false, 0.0f, false),
        new(EUberRenderPreset.Multiply2X, "Multiply 2x", ETransparencyMode.AlphaBlend, ShaderModeTransparent,
            true, EBlendingFactor.DstColor, EBlendingFactor.SrcColor, false, 0.0f, false),
    ];

    /// <summary>
    /// Returns the definitions of all presets, in display order.
    /// </summary>
    public static ReadOnlySpan<UberRenderPresetDefinition> All
        => Definitions;

    /// <summary>
    /// Returns the definition of a preset.
    /// </summary>
    public static ref readonly UberRenderPresetDefinition Get(EUberRenderPreset preset)
    {
        int index = (int)preset;
        if ((uint)index >= (uint)Definitions.Length)
            throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown Uber render preset.");

        return ref Definitions[index];
    }

    /// <summary>
    /// Applies a preset to the material. This sets the transparency mode, alpha cutoff, blend state, depth
    /// write, and the Uber blend mode uniforms.
    /// </summary>
    public static void Apply(XRMaterial material, EUberRenderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(material);
        ref readonly UberRenderPresetDefinition definition = ref Get(preset);

        material.AlphaCutoff = definition.AlphaCutoff;
        // The transparency mode selects the render pass and resets the blend and depth state. The preset blend
        // state replaces that state after.
        material.TransparencyMode = definition.TransparencyMode;
        RenderingParameters options = material.RenderOptions ??= new RenderingParameters();
        options.DepthTest ??= new DepthTest();
        options.DepthTest.UpdateDepth = definition.WritesDepth;
        options.BlendModesPerDrawBuffer = null;
        options.BlendModeAllDrawBuffers = CreateBlendMode(definition);

        SetShaderMode(material, preset);
        material.Parameter<ShaderFloat>("_AlphaPremultiply")?.SetValue(definition.Premultiply ? 1.0f : 0.0f);
    }

    /// <summary>
    /// Sets only the <c>_Mode</c> uniform of the preset. Use this after the render state is already set, for
    /// example by an importer that keeps authored blend factors.
    /// </summary>
    public static void SetShaderMode(XRMaterial material, EUberRenderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(material);
        material.Parameter<ShaderInt>("_Mode")?.SetValue(Get(preset).ShaderMode);
    }

    /// <summary>
    /// Finds the preset that matches the render state of the material. Returns false when the state was
    /// changed after a preset was applied, or when no preset was applied.
    /// </summary>
    public static bool TryDetect(XRMaterial material, out EUberRenderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(material);
        int shaderMode = material.Parameter<ShaderInt>("_Mode")?.Value ?? -1;
        BlendMode? blend = material.RenderOptions?.BlendModeAllDrawBuffers;
        bool blendEnabled = blend?.Enabled == ERenderParamUsage.Enabled;
        bool writesDepth = material.RenderOptions?.DepthTest?.UpdateDepth ?? true;

        foreach (ref readonly UberRenderPresetDefinition definition in Definitions.AsSpan())
        {
            if (definition.TransparencyMode != material.TransparencyMode ||
                definition.ShaderMode != shaderMode ||
                definition.WritesDepth != writesDepth ||
                definition.Blend != blendEnabled)
                continue;
            if (definition.Blend &&
                (blend!.RgbSrcFactor != definition.SourceFactor || blend.RgbDstFactor != definition.DestinationFactor))
                continue;

            preset = definition.Preset;
            return true;
        }

        preset = default;
        return false;
    }

    private static BlendMode CreateBlendMode(in UberRenderPresetDefinition definition)
    {
        if (!definition.Blend)
            return BlendMode.Disabled();

        // The alpha channel keeps the larger value, so a blended surface does not lower the destination alpha.
        return new BlendMode
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = definition.SourceFactor,
            RgbDstFactor = definition.DestinationFactor,
            AlphaSrcFactor = EBlendingFactor.One,
            AlphaDstFactor = EBlendingFactor.One,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.Max,
        };
    }
}
