using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Reads an unlit source without interpreting an arbitrary shader or parameter name as permission to cook.</summary>
public sealed class EngineUnlitSurfaceBinding(XRMaterial material)
{
    private MaterialSurfaceTextureBinding? _inferredBinding;

    public bool TryRead(out EngineUnlitSurface surface, out string? reason)
    {
        if (!TryRead(material, out surface, out reason)) return false;
        if (surface.Texture is not { } texture || surface.TextureBinding is not null) return true;
        XRTexture2D sampled = texture is XRTexture2D image ? image : ((XRTexture2DArray)texture).Textures[0];
        ETexWrapMode wrapU = texture is XRTexture2D imageU ? imageU.UWrap : ((XRTexture2DArray)texture).UWrap;
        ETexWrapMode wrapV = texture is XRTexture2D imageV ? imageV.VWrap : ((XRTexture2DArray)texture).VWrap;
        bool srgb = sampled.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8;
        if (_inferredBinding is not { } retained || !ReferenceEquals(retained.Texture, texture) ||
            retained.WrapU != wrapU || retained.WrapV != wrapV || retained.IsSrgb != srgb)
            _inferredBinding = new(EMaterialTextureSemantic.BaseColor, texture, IsSrgb: srgb,
                WrapU: wrapU, WrapV: wrapV);
        surface = surface with { TextureBinding = _inferredBinding };
        return true;
    }

    public static bool TryRead(XRMaterial material, out EngineUnlitSurface surface, out string? reason)
        => TryReadCore(material, material.EngineSemantic, null, out surface, out reason);

    /// <summary>Reads a canonical desktop source before its detached WebGPU copy receives a semantic identity.</summary>
    public static bool TryReadForCook(XRMaterial material, EngineMaterialSemanticIdentity semantic,
        Func<XRTexture2D, XRTexture2D, bool> equivalentTexture,
        out EngineUnlitSurface surface, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(equivalentTexture);
        return TryReadCore(material, semantic, equivalentTexture, out surface, out reason);
    }

    private static bool TryReadCore(XRMaterial material, EngineMaterialSemanticIdentity semantic,
        Func<XRTexture2D, XRTexture2D, bool>? equivalentTexture,
        out EngineUnlitSurface surface, out string? reason)
    {
        surface = default;
        reason = "Unlit requires the exact built-in material shape without an authored vertex, pass, or binding override.";
        if ((material.GetType() != typeof(XRMaterial) && material.GetType() != typeof(PublishedUnlitMaterial)) ||
            !semantic.IsUnlit() ||
            material.BillboardMode != EMeshBillboardMode.None || material.HasSettingVertexUniformHandlers ||
            !material.HasOnlyStandardSurfaceUniformHandlers || material.HasSettingShadowUniformHandlers ||
            material.BindingPublishers.Count != 0 || material.PassSet.Passes.Length != 0 ||
            material.PassSet.DisabledSourcePasses.Length != 0 || material.PassSet.SourceRenderQueue != -1 ||
            material.PassSet.QueuePriority != 0 || material.PassSet.ForwardAddRenderOptions is not null ||
            material.PassSet.ForwardAddPolicy != EMaterialForwardAddPolicy.FoldedIntoForwardPlusBase ||
            material.UberAuthoredState.Features.Length != 0 || material.UberAuthoredState.Properties.Length != 0 ||
            !material.RequestedUberVariant.IsEmpty || material.TransparentTechniqueOverride is not null ||
            material.EmissiveColor.HasValue || material.EmissionStrength.HasValue || material.Transmission != 0 ||
            material.TransmissionColor != Vector3.One || material.NormalScale != 1)
            return false;

        ETransparencyMode mode = material.GetEffectiveTransparencyMode();
        if (mode is not (ETransparencyMode.Opaque or ETransparencyMode.Masked or ETransparencyMode.AlphaBlend or
            ETransparencyMode.PremultipliedAlpha or ETransparencyMode.Additive) ||
            mode == ETransparencyMode.Masked && semantic != EngineMaterialSemanticIdentity.UnlitAlphaTextureV4 ||
            semantic == EngineMaterialSemanticIdentity.UnlitOpaqueTextureV3 && mode != ETransparencyMode.Opaque ||
            (material.RenderPass != (int)EDefaultRenderPass.OpaqueForward &&
             material.RenderPass != (int)EDefaultRenderPass.MaskedForward &&
             material.RenderPass != (int)EDefaultRenderPass.TransparentForward))
        {
            reason = "Unlit requires an opaque, masked, or painter-ordered forward pass and ordinary authored blend state.";
            return false;
        }
        if (mode == ETransparencyMode.Masked && material.RenderPass != (int)EDefaultRenderPass.MaskedForward ||
            (mode is ETransparencyMode.AlphaBlend or ETransparencyMode.PremultipliedAlpha or ETransparencyMode.Additive) &&
                material.RenderPass != (int)EDefaultRenderPass.TransparentForward ||
            mode == ETransparencyMode.Opaque && material.RenderPass != (int)EDefaultRenderPass.OpaqueForward &&
                !(semantic == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4 &&
                  material.RenderPass == (int)EDefaultRenderPass.TransparentForward))
        {
            reason = "Unlit forward pass ordering must match its authored opaque, masked, or sorted coverage mode.";
            return false;
        }
        RenderingParameters options = material.RenderOptions;
        bool sorted = mode is ETransparencyMode.AlphaBlend or ETransparencyMode.PremultipliedAlpha or ETransparencyMode.Additive;
        BlendMode? blend = options.BlendModeAllDrawBuffers;
        if (options.DepthTest.Enabled != ERenderParamUsage.Enabled || options.DepthTest.Function != EComparison.Lequal ||
            options.DepthTest.UpdateDepth == sorted || options.AlphaToCoverage == ERenderParamUsage.Enabled ||
            options.BlendModesPerDrawBuffer is not null || (blend?.Enabled == ERenderParamUsage.Enabled) != sorted ||
            sorted && (blend!.RgbEquation != EBlendEquationMode.FuncAdd || blend.AlphaEquation != EBlendEquationMode.FuncAdd ||
                blend.RgbSrcFactor != (mode == ETransparencyMode.PremultipliedAlpha ? EBlendingFactor.One : EBlendingFactor.SrcAlpha) ||
                blend.AlphaSrcFactor != (mode == ETransparencyMode.PremultipliedAlpha ? EBlendingFactor.One : EBlendingFactor.SrcAlpha) ||
                blend.RgbDstFactor != (mode == ETransparencyMode.Additive ? EBlendingFactor.One : EBlendingFactor.OneMinusSrcAlpha) ||
                blend.AlphaDstFactor != (mode == ETransparencyMode.Additive ? EBlendingFactor.One : EBlendingFactor.OneMinusSrcAlpha)))
        {
            reason = "Unlit requires authored Lequal depth, its selected depth-write mode, and the matching ordinary blend factors.";
            return false;
        }

        Vector4 color = Vector4.One;
        float cutoff = 0.1f;
        if (semantic == EngineMaterialSemanticIdentity.UnlitColorV1)
        {
            if (material.Parameters.Length != 1 || material.Parameters[0] is not ShaderVector4 { Name: "MatColor" } tint ||
                material.Textures.Count != 0 || material.SurfaceTextureBindings.Length != 0)
                return false;
            color = tint.Value;
        }
        else
        {
            if (!TryReadCanonicalTextureParameters(material.Parameters, semantic, out cutoff))
            {
                reason = "Unlit requires only the exact typed uniforms declared by its canonical fragment.";
                return false;
            }
            if (semantic == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4 &&
                mode == ETransparencyMode.Masked && cutoff != material.AlphaCutoff)
            {
                reason = "Masked unlit alpha requires the canonical coverage cutoff and Texture0 cutoff to agree.";
                return false;
            }
            if (material.Textures.Count != 1 || material.Textures[0] is not { } texture ||
                texture.ResolveSamplerName(0) != "Texture0" ||
                (semantic == EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5
                    ? texture.GetType() != typeof(XRTexture2DArray) || ((XRTexture2DArray)texture).Textures.Length is < 1 or > 64
                    : texture.GetType() != typeof(XRTexture2D)))
            {
                reason = "Unlit Texture0 requires one static 2D image or, for the array variant, a nonempty 2D array.";
                return false;
            }
            MaterialSurfaceTextureBinding? binding = null;
            if (material.SurfaceTextureBindings.Length == 1)
            {
                binding = material.SurfaceTextureBindings[0];
                bool same = ReferenceEquals(binding.Texture, texture) || equivalentTexture is not null &&
                    binding.Texture is XRTexture2D bound && texture is XRTexture2D original &&
                    equivalentTexture(bound, original);
                ETexWrapMode wrapU = texture is XRTexture2D imageU ? imageU.UWrap : ((XRTexture2DArray)texture).UWrap;
                ETexWrapMode wrapV = texture is XRTexture2D imageV ? imageV.VWrap : ((XRTexture2DArray)texture).VWrap;
                if (!same || binding.Semantic != EMaterialTextureSemantic.BaseColor || binding.TexCoordSet != 0 ||
                    binding.Channel != 0 || binding.UvScaleOffset != new Vector4(1, 1, 0, 0) || binding.UvRotation != 0 ||
                    binding.WrapU != wrapU || binding.WrapV != wrapV)
                {
                    reason = "Unlit Texture0 requires one identity-transform base-color role when metadata is present.";
                    return false;
                }
            }
            else if (material.SurfaceTextureBindings.Length != 0)
                return false;
            if (!float.IsFinite(cutoff) || cutoff is < 0 or > 1)
            {
                reason = "Unlit alpha cutoff must be finite and within zero to one.";
                return false;
            }
            surface = new(semantic, color, texture, binding, cutoff, mode);
            reason = null;
            return true;
        }
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) || !float.IsFinite(color.W))
        {
            reason = "Unlit MatColor must be finite.";
            return false;
        }
        surface = new(semantic, color, null, null, cutoff, mode);
        reason = null;
        return true;
    }

    private static bool TryReadCanonicalTextureParameters(ShaderVar[] parameters,
        EngineMaterialSemanticIdentity semantic, out float cutoff)
    {
        cutoff = 0.1f;
        if (semantic == EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5)
            return parameters.Length == 0;
        if (parameters.Length == 0) return true;
        bool alpha = semantic == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4;
        if (alpha && parameters.Length == 1 && parameters[0]?.GetType() == typeof(ShaderFloat) &&
            parameters[0].Name == "AlphaCutoff")
        {
            cutoff = ((ShaderFloat)parameters[0]).Value;
            return true;
        }
        if (parameters.Length != (alpha ? 14 : 4)) return false;
        uint seen = 0;
        foreach (ShaderVar? parameter in parameters)
        {
            if (parameter is null) return false;
            int slot = parameter.Name switch
            {
                "PpllMaxNodes" when parameter.GetType() == typeof(ShaderUInt) => 0,
                "DepthPeelLayerIndex" when parameter.GetType() == typeof(ShaderInt) => 1,
                "DepthPeelEpsilon" when parameter.GetType() == typeof(ShaderFloat) => 2,
                "DepthPeelReversedDepth" when parameter.GetType() == typeof(ShaderBool) => 3,
                "AlphaCutoff" when alpha && parameter.GetType() == typeof(ShaderFloat) => 4,
                "LightPos" when alpha && parameter.GetType() == typeof(ShaderVector3) => 5,
                "FarPlaneDist" when alpha && parameter.GetType() == typeof(ShaderFloat) => 6,
                "ShadowMapEncoding" when alpha && parameter.GetType() == typeof(ShaderInt) => 7,
                "ShadowMomentMinVariance" when alpha && parameter.GetType() == typeof(ShaderFloat) => 8,
                "ShadowMomentLightBleedReduction" when alpha && parameter.GetType() == typeof(ShaderFloat) => 9,
                "ShadowMomentPositiveExponent" when alpha && parameter.GetType() == typeof(ShaderFloat) => 10,
                "ShadowMomentNegativeExponent" when alpha && parameter.GetType() == typeof(ShaderFloat) => 11,
                "ShadowMomentMipBias" when alpha && parameter.GetType() == typeof(ShaderFloat) => 12,
                "ShadowDepthSourceMode" when alpha && parameter.GetType() == typeof(ShaderInt) => 13,
                _ => -1,
            };
            if (slot < 0 || (seen & (1u << slot)) != 0) return false;
            seen |= 1u << slot;
            if (slot == 4) cutoff = ((ShaderFloat)parameter).Value;
        }
        return seen == (alpha ? (1u << 14) - 1 : (1u << 4) - 1);
    }
}
