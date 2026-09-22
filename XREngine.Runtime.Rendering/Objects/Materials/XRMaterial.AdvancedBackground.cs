using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private AdvancedBackgroundMaterialProfile? _advancedBackgroundProfile;
    private RenderingParameters? _multisampleBackgroundParameters;

    /// <summary>
    /// Blends the one admitted authored background into the uncovered sample fraction
    /// left in native HDR alpha. Keep the admitted material untouched: its ordinary
    /// far-depth state is still required by single-sample cameras sharing the same material.
    /// </summary>
    internal RenderingParameters GetMultisampleBackgroundParameters()
    {
        // One cached state object per material, never one allocation per draw.
        RenderingParameters parameters = _multisampleBackgroundParameters ??= new()
        {
            DepthTest = new() { Enabled = ERenderParamUsage.Disabled, UpdateDepth = false },
            StencilTest = new() { Enabled = ERenderParamUsage.Disabled },
            BlendModeAllDrawBuffers = new()
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbEquation = EBlendEquationMode.FuncAdd,
                AlphaEquation = EBlendEquationMode.FuncAdd,
                RgbSrcFactor = EBlendingFactor.OneMinusDstAlpha,
                RgbDstFactor = EBlendingFactor.One,
                AlphaSrcFactor = EBlendingFactor.OneMinusDstAlpha,
                AlphaDstFactor = EBlendingFactor.One,
            },
            WriteAlpha = true,
        };
        parameters.CullMode = RenderOptions.CullMode;
        parameters.Winding = RenderOptions.Winding;
        parameters.RequiredEngineUniforms = RenderOptions.RequiredEngineUniforms;
        parameters.WriteRed = RenderOptions.WriteRed;
        parameters.WriteGreen = RenderOptions.WriteGreen;
        parameters.WriteBlue = RenderOptions.WriteBlue;
        return parameters;
    }

    /// <summary>Explicit far-depth shader contract for the Advanced authored background lane.</summary>
    public AdvancedBackgroundMaterialProfile? AdvancedBackgroundProfile
    {
        get => _advancedBackgroundProfile;
        set => SetField(ref _advancedBackgroundProfile, value);
    }

    /// <summary>
    /// Validates the shader receipt and state that preserve native opaque identity and alpha.
    /// Lequal is the engine's canonical comparison; the backend maps it to Gequal
    /// when the active camera uses reversed depth, matching the sky shader's far clip.
    /// </summary>
    public bool TryValidateAdvancedBackground(bool stereo, out string? reason)
    {
        reason = GetAdvancedBackgroundRejection(stereo);
        return reason is null;
    }

    private string? GetAdvancedBackgroundRejection(bool stereo)
    {
        AdvancedBackgroundMaterialProfile? profile = AdvancedBackgroundProfile;
        if (profile is null)
            return "The material has no explicit Advanced far-depth background shader receipt.";
        if (profile.UnsupportedReason is not null)
            return profile.UnsupportedReason;
        if (!profile.WritesOpaqueAlpha)
            return "The background shader receipt does not guarantee an opaque HDR alpha coverage value.";
        if (profile.SourceShaderRevision != ShaderStateRevision)
            return "The background shader changed after admission; recreate its far-depth shader receipt.";
        if (stereo && !profile.SupportsStereo)
            return "The background shader has no admitted stereo variant.";
        if (RenderPass != (int)EDefaultRenderPass.Background)
            return "The material must use the Background draw bucket.";

        RenderingParameters options = RenderOptions;
        if (options.DepthTest.Enabled != ERenderParamUsage.Enabled || options.DepthTest.UpdateDepth || options.DepthTest.Function != EComparison.Lequal)
            return "Background drawing requires depth testing, canonical Lequal comparison, and disabled depth writes.";
        if (options.WriteAlpha || options.StencilTest.Enabled != ERenderParamUsage.Disabled)
            return "Background drawing must preserve native alpha and stencil.";
        if (options.BlendModeAllDrawBuffers?.Enabled != ERenderParamUsage.Disabled || options.BlendModesPerDrawBuffer?.Count > 0)
            return "Background drawing requires explicitly disabled blending without per-attachment overrides.";
        return null;
    }
}
