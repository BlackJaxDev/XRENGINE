using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Reads the explicit forward texture-alpha schema without inferring behavior from arbitrary shader text.</summary>
public sealed class TexturedAlphaSurfaceBinding(XRMaterial material)
{
    public static bool TryCreate(XRMaterial material, out TexturedAlphaSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!TryRead(material, out _, out reason)) { binding = null; return false; }
        binding = new(material);
        return true;
    }

    public bool TryRead(out TexturedAlphaSurface surface, out string? reason)
        => TryRead(material, out surface, out reason);

    public static bool TryRead(XRMaterial material, out TexturedAlphaSurface surface, out string? reason)
        => TryReadCore(material, null, out surface, out reason);

    /// <summary>Allows only aliases proven equivalent by the offline cook; live reads require reference identity.</summary>
    public static bool TryReadForCook(XRMaterial material, Func<XRTexture2D, XRTexture2D, bool> equivalentTexture,
        out TexturedAlphaSurface surface, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(equivalentTexture);
        return TryReadCore(material, equivalentTexture, out surface, out reason);
    }

    private static bool TryReadCore(XRMaterial material, Func<XRTexture2D, XRTexture2D, bool>? equivalentTexture,
        out TexturedAlphaSurface surface, out string? reason)
    {
        surface = default;
        reason = "AuthoredLitTextureAlphaV1 requires its explicit canonical forward surface without custom bindings, vertex behavior or extensions.";
        ETransparencyMode mode = material.GetEffectiveTransparencyMode();
        if (material is not (AuthoredTexturedAlphaMaterial or PublishedTexturedAlphaMaterial) ||
            material is AuthoredTexturedAlphaMaterial { HasAuthoredTextureSettings: false } ||
            material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1 ||
            mode is not (ETransparencyMode.Masked or ETransparencyMode.AlphaBlend) ||
            material.RenderPass != (int)(mode == ETransparencyMode.Masked ? EDefaultRenderPass.MaskedForward : EDefaultRenderPass.TransparentForward) ||
            material.Shaders.Count is < 1 or > 2 ||
            material.BillboardMode != EMeshBillboardMode.None || material.TransparentTechniqueOverride is not null ||
            material.EmissiveColor.HasValue || material.EmissionStrength.HasValue ||
            material.Transmission != 0 || material.TransmissionColor != Vector3.One || material.NormalScale != 1 ||
            !material.HasOnlyStandardSurfaceUniformHandlers || material.HasSettingShadowUniformHandlers || material.HasSettingVertexUniformHandlers ||
            material.BindingPublishers.Count != 0 || material.PassSet.Passes.Length != 0 ||
            material.PassSet.DisabledSourcePasses.Length != 0 || material.PassSet.SourceRenderQueue != -1 ||
            material.PassSet.QueuePriority != 0 || material.PassSet.ForwardAddRenderOptions is not null ||
            material.PassSet.ForwardAddPolicy != EMaterialForwardAddPolicy.FoldedIntoForwardPlusBase ||
            material.UberAuthoredState.Features.Length != 0 || material.UberAuthoredState.Properties.Length != 0 ||
            !material.RequestedUberVariant.IsEmpty)
            return false;

        reason = "AuthoredLitTextureAlphaV1 requires exactly UV0 identity-transform diffuse RGBA and linear red opacity roles with matching Texture0/Texture1 images and sampler wrapping.";
        if (material.SurfaceTextureBindings.Length != 2 || material.Textures.Count != 2)
            return false;
        MaterialSurfaceTextureBinding? baseColor = null, opacity = null;
        foreach (MaterialSurfaceTextureBinding? binding in material.SurfaceTextureBindings)
        {
            if (binding is null || binding.Texture is not XRTexture2D texture || texture.GetType() != typeof(XRTexture2D) ||
                binding.TexCoordSet != 0 || binding.Channel != 0 || binding.UvScaleOffset != new Vector4(1, 1, 0, 0) ||
                binding.UvRotation != 0 || binding.WrapU != texture.UWrap || binding.WrapV != texture.VWrap ||
                !HasExactSampler(texture))
                return false;
            if (binding.Semantic == EMaterialTextureSemantic.BaseColor && baseColor is null)
                baseColor = binding;
            else if (binding.Semantic == EMaterialTextureSemantic.Opacity && opacity is null && !binding.IsSrgb &&
                texture.SizedInternalFormat is not (ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8))
                opacity = binding;
            else return false;
        }
        if (baseColor is null || opacity is null ||
            !SameTexture(material.Textures[0], baseColor.Texture, equivalentTexture) ||
            !SameTexture(material.Textures[1], opacity.Texture, equivalentTexture) ||
            baseColor.Texture.ResolveSamplerName(0) != "Texture0" || opacity.Texture.ResolveSamplerName(1) != "Texture1")
            return false;

        reason = "AuthoredLitTextureAlphaV1 requires exactly six finite float parameters: MatSpecularIntensity, MatShininess, AlphaCutoff, Roughness, Metallic and Emission; cutoff must match AlphaCutoff.";
        if (material.Parameters.Length != 6) return false;
        float specular = 0, shininess = 0, cutoff = 0, roughness = 0, metallic = 0, emission = 0;
        int bits = 0;
        foreach (ShaderVar? parameter in material.Parameters)
        {
            if (parameter?.GetType() != typeof(ShaderFloat) || !float.IsFinite(((ShaderFloat)parameter).Value)) return false;
            float value = ((ShaderFloat)parameter).Value;
            int bit = parameter.Name switch
            {
                "MatSpecularIntensity" => 1, "MatShininess" => 2, "AlphaCutoff" => 4,
                "Roughness" => 8, "Metallic" => 16, "Emission" => 32, _ => 0,
            };
            if (bit == 0 || (bits & bit) != 0) return false;
            bits |= bit;
            switch (bit)
            {
                case 1: specular = value; break;
                case 2: shininess = value; break;
                case 4: cutoff = value; break;
                case 8: roughness = value; break;
                case 16: metallic = value; break;
                case 32: emission = value; break;
            }
        }
        if (bits != 63 || cutoff != material.AlphaCutoff) return false;
        surface = new(new(StandardLitColorSurfaceSchema.Forward, Vector3.One, 1, specular, roughness,
            metallic, emission, 1, mode, cutoff), baseColor, opacity, shininess);
        reason = null;
        return true;
    }

    private static bool SameTexture(XRTexture? left, XRTexture right, Func<XRTexture2D, XRTexture2D, bool>? equivalentTexture)
        => ReferenceEquals(left, right) || left is XRTexture2D left2D && right is XRTexture2D right2D &&
            equivalentTexture is not null && equivalentTexture(left2D, right2D);

    private static bool HasExactSampler(XRTexture2D texture)
    {
        if (texture.EnableComparison || texture.LodBias != 0 ||
            texture.UWrap is not (ETexWrapMode.Repeat or ETexWrapMode.MirroredRepeat or ETexWrapMode.ClampToEdge) ||
            texture.VWrap is not (ETexWrapMode.Repeat or ETexWrapMode.MirroredRepeat or ETexWrapMode.ClampToEdge) ||
            texture.MagFilter is not (ETexMagFilter.Nearest or ETexMagFilter.Linear) ||
            texture.MinFilter is not (ETexMinFilter.Nearest or ETexMinFilter.Linear or ETexMinFilter.NearestMipmapNearest or
                ETexMinFilter.LinearMipmapNearest or ETexMinFilter.NearestMipmapLinear or ETexMinFilter.LinearMipmapLinear) ||
            !float.IsFinite(texture.MaxAnisotropy) || texture.MaxAnisotropy is < 1 or > 16 ||
            texture.MaxAnisotropy != MathF.Truncate(texture.MaxAnisotropy) ||
            texture.MaxAnisotropy > 1 && (texture.MinFilter != ETexMinFilter.LinearMipmapLinear || texture.MagFilter != ETexMagFilter.Linear) ||
            !float.IsFinite(texture.MinLOD) || !float.IsFinite(texture.MaxLOD))
            return false;
        bool mipmapped = texture.MinFilter is not (ETexMinFilter.Nearest or ETexMinFilter.Linear);
        return mipmapped ? Math.Max(texture.MinLOD, 0) <= Math.Min(texture.MaxLOD, 32)
            : texture.MinLOD <= 0 && texture.MaxLOD >= 0;
    }
}
