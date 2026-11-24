using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Describes the render state that an Uber render preset applies.
/// </summary>
/// <param name="Preset">The preset.</param>
/// <param name="DisplayName">The name that the editor shows.</param>
/// <param name="TransparencyMode">The engine transparency mode. It selects the render pass and depth write.</param>
/// <param name="ShaderMode">The value of the <c>_Mode</c> uniform.</param>
/// <param name="Blend">True when the preset blends with the destination.</param>
/// <param name="SourceFactor">The color source blend factor.</param>
/// <param name="DestinationFactor">The color destination blend factor.</param>
/// <param name="WritesDepth">True when the preset writes depth.</param>
/// <param name="AlphaCutoff">The alpha cutoff that the preset sets.</param>
/// <param name="Premultiply">True when the shader multiplies the color by alpha before blending.</param>
public readonly record struct UberRenderPresetDefinition(
    EUberRenderPreset Preset,
    string DisplayName,
    ETransparencyMode TransparencyMode,
    int ShaderMode,
    bool Blend,
    EBlendingFactor SourceFactor,
    EBlendingFactor DestinationFactor,
    bool WritesDepth,
    float AlphaCutoff,
    bool Premultiply);
