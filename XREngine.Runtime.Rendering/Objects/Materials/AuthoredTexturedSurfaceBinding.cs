using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Reads explicit canonical forward texture roles and factors without inferring arbitrary shader behavior.</summary>
public sealed class AuthoredTexturedSurfaceBinding(XRMaterial material)
{
    public static bool TryCreate(XRMaterial material, out AuthoredTexturedSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!TryRead(material, out _, out reason)) { binding = null; return false; }
        binding = new(material);
        return true;
    }

    public bool TryRead(out AuthoredTexturedSurface surface, out string? reason)
        => TryRead(material, out surface, out reason);

    public static bool TryRead(XRMaterial material, out AuthoredTexturedSurface surface, out string? reason)
        => TryReadCore(material, null, out surface, out reason);

    /// <summary>Allows aliases only after the offline cook proves equal identities, payloads and full carried settings.</summary>
    public static bool TryReadForCook(XRMaterial material, Func<XRTexture2D, XRTexture2D, bool> equivalentTexture,
        out AuthoredTexturedSurface surface, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(equivalentTexture);
        return TryReadCore(material, equivalentTexture, out surface, out reason);
    }

    private static bool TryReadCore(XRMaterial material, Func<XRTexture2D, XRTexture2D, bool>? equivalentTexture,
        out AuthoredTexturedSurface surface, out string? reason)
    {
        surface = default;
        reason = "AuthoredLitTexturedV1 requires its explicit canonical forward surface without custom bindings, vertex behavior or extensions.";
        ETransparencyMode mode = material.GetEffectiveTransparencyMode();
        if (material is not (AuthoredTexturedMaterial or PublishedAuthoredTexturedMaterial) ||
            material is AuthoredTexturedMaterial { HasAuthoredTextureSettings: false } ||
            material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitTexturedV1 ||
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

        reason = "AuthoredLitTexturedV1 requires UV0 identity-transform diffuse and normal/height or specular roles, optional opacity, exact contiguous sampler slots, and linear non-color images.";
        if (material.SurfaceTextureBindings.Length is < 2 or > 4 || material.Textures.Count != material.SurfaceTextureBindings.Length)
            return false;
        MaterialSurfaceTextureBinding? baseColor = null, normal = null, specularMap = null, opacity = null;
        foreach (MaterialSurfaceTextureBinding? binding in material.SurfaceTextureBindings)
        {
            if (binding is null || binding.Texture is not XRTexture2D texture || texture.GetType() != typeof(XRTexture2D) ||
                binding.TexCoordSet != 0 || binding.Channel != 0 || binding.UvScaleOffset != new Vector4(1, 1, 0, 0) ||
                binding.UvRotation != 0 || binding.WrapU != texture.UWrap || binding.WrapV != texture.VWrap ||
                !HasExactSampler(texture))
                return false;
            if (binding.Semantic == EMaterialTextureSemantic.BaseColor && baseColor is null)
                baseColor = binding;
            else
            {
                if (binding.IsSrgb || texture.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8)
                    return false;
                if (binding.Semantic == EMaterialTextureSemantic.Normal && normal is null) normal = binding;
                else if (binding.Semantic == EMaterialTextureSemantic.Specular && specularMap is null) specularMap = binding;
                else if (binding.Semantic == EMaterialTextureSemantic.Opacity && opacity is null) opacity = binding;
                else return false;
            }
        }
        if (baseColor is null || normal is null && specularMap is null) return false;
        int slot = 0;
        if (!MatchesSlot(material, baseColor, slot++, equivalentTexture) ||
            normal is not null && !MatchesSlot(material, normal, slot++, equivalentTexture) ||
            specularMap is not null && !MatchesSlot(material, specularMap, slot++, equivalentTexture) ||
            opacity is not null && !MatchesSlot(material, opacity, slot++, equivalentTexture))
            return false;
        for (int left = 0; left < material.Textures.Count; left++)
            for (int right = left + 1; right < material.Textures.Count; right++)
                if (material.Textures[left] is XRTexture2D a && material.Textures[right] is XRTexture2D b &&
                    a.ID == b.ID && !SameTexture(a, b, equivalentTexture))
                    return false;

        reason = "AuthoredLitTexturedV1 requires opaque or sorted blend without opacity, masked or sorted blend with opacity, and the corresponding forward render pass.";
        if (opacity is null ? mode is not (ETransparencyMode.Opaque or ETransparencyMode.AlphaBlend)
            : mode is not (ETransparencyMode.Masked or ETransparencyMode.AlphaBlend))
            return false;
        int expectedPass = (int)(mode == ETransparencyMode.Opaque ? EDefaultRenderPass.OpaqueForward
            : mode == ETransparencyMode.Masked ? EDefaultRenderPass.MaskedForward : EDefaultRenderPass.TransparentForward);
        if (material.RenderPass != expectedPass) return false;

        reason = "AuthoredLitTexturedV1 requires exact finite authored float parameters, AlphaCutoff only with opacity, and NormalMapMode zero/one plus finite HeightMapScale only with a normal/height image.";
        if (material.Parameters.Length != 5 + (opacity is null ? 0 : 1) + (normal is null ? 0 : 2) || !float.IsFinite(material.AlphaCutoff))
            return false;
        float specular = 0, shininess = 0, roughness = 0, metallic = 0, emission = 0, cutoff = material.AlphaCutoff, heightScale = 1;
        int bits = 0, normalMode = 0;
        foreach (ShaderVar? parameter in material.Parameters)
        {
            if (parameter?.GetType() == typeof(ShaderInt) && parameter.Name == "NormalMapMode" && normal is not null && (bits & 64) == 0)
            {
                normalMode = ((ShaderInt)parameter).Value;
                if (normalMode is not (0 or 1)) return false;
                bits |= 64;
                continue;
            }
            if (parameter?.GetType() != typeof(ShaderFloat) || !float.IsFinite(((ShaderFloat)parameter).Value)) return false;
            float value = ((ShaderFloat)parameter).Value;
            int bit = parameter.Name switch
            {
                "MatSpecularIntensity" => 1, "MatShininess" => 2, "Roughness" => 4, "Metallic" => 8, "Emission" => 16,
                "AlphaCutoff" when opacity is not null => 32, "HeightMapScale" when normal is not null => 128, _ => 0,
            };
            if (bit == 0 || (bits & bit) != 0) return false;
            bits |= bit;
            switch (bit)
            {
                case 1: specular = value; break;
                case 2: shininess = value; break;
                case 4: roughness = value; break;
                case 8: metallic = value; break;
                case 16: emission = value; break;
                case 32: cutoff = value; break;
                case 128: heightScale = value; break;
            }
        }
        if (bits != (31 | (opacity is null ? 0 : 32) | (normal is null ? 0 : 192)) || cutoff != material.AlphaCutoff) return false;
        surface = new(new(StandardLitColorSurfaceSchema.Forward, Vector3.One, 1, specular, roughness,
            metallic, emission, 1, mode, cutoff), baseColor, normal, specularMap, opacity, shininess, normalMode, heightScale);
        reason = null;
        return true;
    }

    private static bool MatchesSlot(XRMaterial material, MaterialSurfaceTextureBinding binding, int slot,
        Func<XRTexture2D, XRTexture2D, bool>? equivalentTexture)
        => SameTexture(material.Textures[slot], binding.Texture, equivalentTexture) &&
            binding.Texture.ResolveSamplerName(slot) == (slot switch { 0 => "Texture0", 1 => "Texture1", 2 => "Texture2", 3 => "Texture3", _ => null });

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
