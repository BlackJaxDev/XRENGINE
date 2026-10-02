using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Reads only explicitly tagged opaque deferred textures. Bounded reference checks on every
/// read detect in-place edits to public parameter and texture arrays without allocating.
/// Desktop shader selection remains the author's responsibility.
/// </summary>
public sealed class StandardLitTextureSurfaceBinding(XRMaterial material, bool authoredCooked = false)
{
    public static bool TryCreate(XRMaterial material, out StandardLitTextureSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        StandardLitTextureSurfaceBinding candidate = new(material);
        if (!candidate.TryRead(out _, out reason)) { binding = null; return false; }
        binding = candidate;
        return true;
    }

    /// <summary>Reads exact authored PBR texture inputs when cooked stages own shader selection.</summary>
    public static bool TryCreateAuthoredCooked(XRMaterial material, out StandardLitTextureSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        StandardLitTextureSurfaceBinding candidate = new(material, authoredCooked: true);
        if (!candidate.TryRead(out _, out reason)) { binding = null; return false; }
        binding = candidate;
        return true;
    }

    public bool TryRead(out StandardLitTextureSurface surface, out string? reason)
        => TryReadCore(material, null, authoredCooked, out surface, out reason);

    /// <summary>Checks a live surface without allocating a reader, including per-face shadow admission.</summary>
    public static bool TryRead(XRMaterial material, out StandardLitTextureSurface surface, out string? reason)
        => TryReadCore(material, null, false, out surface, out reason);

    /// <summary>
    /// Checks the same schema during offline cooking. The owner must prove complete texture
    /// equivalence before accepting a serialized alias and strictly validate its detached result.
    /// Live material readers always require reference identity.
    /// </summary>
    public static bool TryReadForCook(XRMaterial material, Func<XRTexture2D, XRTexture2D, bool> equivalentTexture,
        out StandardLitTextureSurface surface, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(equivalentTexture);
        return TryReadCore(material, equivalentTexture, false, out surface, out reason);
    }

    /// <summary>Validates alias-equivalent authored textures before the cook creates a detached carrier.</summary>
    public static bool TryReadAuthoredForCook(XRMaterial material, Func<XRTexture2D, XRTexture2D, bool> equivalentTexture,
        out StandardLitTextureSurface surface, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(equivalentTexture);
        return TryReadCore(material, equivalentTexture, true, out surface, out reason);
    }

    private static bool TryReadCore(XRMaterial material, Func<XRTexture2D, XRTexture2D, bool>? equivalentTexture, bool authoredCooked,
        out StandardLitTextureSurface surface, out string? reason)
    {
        surface = default;
        reason = "StandardLitTextureV1 requires its exact opaque deferred surface without custom bindings or extensions.";
        if ((authoredCooked ? material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitV1 || material.Shaders.Count == 0 :
            material.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitTextureV1) ||
            material.RenderPass != (int)EDefaultRenderPass.OpaqueDeferred ||
            material.GetEffectiveTransparencyMode() != ETransparencyMode.Opaque ||
            material.BillboardMode != EMeshBillboardMode.None ||
            material.TransparentTechniqueOverride is not null || material.AlphaCutoff != 0.5f ||
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

        reason = "StandardLitTextureV1 requires unique UV0, identity-transform base/normal/metallic/roughness bindings with unchanged sampler wrapping.";
        MaterialSurfaceTextureBinding? albedo = null, normal = null, metallic = null, roughness = null;
        int textureBits = 0;
        foreach (MaterialSurfaceTextureBinding? binding in material.SurfaceTextureBindings)
        {
            if (binding is null || binding.Texture is not XRTexture2D texture || binding.TexCoordSet != 0 ||
                binding.Channel != 0 || binding.UvScaleOffset != new Vector4(1, 1, 0, 0) || binding.UvRotation != 0 ||
                binding.WrapU != texture.UWrap || binding.WrapV != texture.VWrap ||
                binding.Semantic != EMaterialTextureSemantic.BaseColor && (binding.IsSrgb ||
                    texture.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8))
                return false;
            int bit = binding.Semantic switch
            {
                EMaterialTextureSemantic.BaseColor => 1,
                EMaterialTextureSemantic.Normal => 2,
                EMaterialTextureSemantic.Metallic => 4,
                EMaterialTextureSemantic.Roughness => 8,
                _ => 0,
            };
            if (bit == 0 || (textureBits & bit) != 0) return false;
            textureBits |= bit;
            switch (bit)
            {
                case 1: albedo = binding; break;
                case 2: normal = binding; break;
                case 4: metallic = binding; break;
                case 8: roughness = binding; break;
            }
        }
        if (albedo is null) return false;

        reason = "StandardLitTextureV1 legacy sampler slots must match the authored deferred texture family exactly.";
        int expectedCount = roughness is not null ? (normal is not null || metallic is not null ? 4 : 3)
            : metallic is not null ? 3 : normal is not null ? 2 : 1;
        if (material.Textures.Count != expectedCount || !SameTexture(material.Textures[0], albedo.Texture, equivalentTexture) ||
            expectedCount > 1 && !SameTexture(material.Textures[1], normal?.Texture, equivalentTexture) ||
            expectedCount > 2 && !SameTexture(material.Textures[2], (metallic ?? (normal is null ? roughness : null))?.Texture, equivalentTexture) ||
            expectedCount > 3 && !SameTexture(material.Textures[3], roughness?.Texture, equivalentTexture))
            return false;

        reason = "StandardLitTextureV1 requires six deferred numeric inputs; RGB-normal materials also require NormalMapMode=0 and HeightMapScale=0.";
        ShaderVar[] parameters = material.Parameters;
        if (parameters.Length != (normal is null ? 6 : 8)) return false;
        Vector3 color = default;
        float opacity = 0, specular = 0, roughnessValue = 0, metallicValue = 0, emission = 0;
        int parameterBits = 0;
        foreach (ShaderVar? parameter in parameters)
        {
            int bit = parameter?.Name switch
            {
                "BaseColor" when parameter.GetType() == typeof(ShaderVector3) => 1,
                "Opacity" when parameter.GetType() == typeof(ShaderFloat) => 2,
                "Specular" when parameter.GetType() == typeof(ShaderFloat) => 4,
                "Roughness" when parameter.GetType() == typeof(ShaderFloat) => 8,
                "Metallic" when parameter.GetType() == typeof(ShaderFloat) => 16,
                "Emission" when parameter.GetType() == typeof(ShaderFloat) => 32,
                "NormalMapMode" when normal is not null && parameter is ShaderInt { Value: 0 } => 64,
                "HeightMapScale" when normal is not null && parameter is ShaderFloat { Value: 0 } => 128,
                _ => 0,
            };
            if (bit == 0 || (parameterBits & bit) != 0) return false;
            parameterBits |= bit;
            switch (bit)
            {
                case 1: color = ((ShaderVector3)parameter!).Value; break;
                case 2: opacity = ((ShaderFloat)parameter!).Value; break;
                case 4: specular = ((ShaderFloat)parameter!).Value; break;
                case 8: roughnessValue = ((ShaderFloat)parameter!).Value; break;
                case 16: metallicValue = ((ShaderFloat)parameter!).Value; break;
                case 32: emission = ((ShaderFloat)parameter!).Value; break;
            }
        }
        if (parameterBits != (normal is null ? 63 : 255)) return false;
        reason = "StandardLitTextureV1 requires opacity exactly one and finite base-color and PBR factors; dithered opacity is a separate contract.";
        if (opacity != 1 || !float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) ||
            !float.IsFinite(specular) || !float.IsFinite(roughnessValue) || !float.IsFinite(metallicValue) || !float.IsFinite(emission))
            return false;
        if (normal is not null && roughness is not null && metallic is null && metallicValue != 0)
        {
            reason = "StandardLitTextureV1's sparse metallic slot in the normal/roughness family requires a zero metallic factor.";
            return false;
        }
        surface = new(new(StandardLitColorSurfaceSchema.DeferredEmission, color, opacity, specular,
            roughnessValue, metallicValue, emission, 1), albedo, normal, metallic, roughness);
        reason = null;
        return true;
    }

    private static bool SameTexture(XRTexture? left, XRTexture? right, Func<XRTexture2D, XRTexture2D, bool>? equivalentTexture)
        => ReferenceEquals(left, right) || left is XRTexture2D left2D && right is XRTexture2D right2D &&
            equivalentTexture is not null && equivalentTexture(left2D, right2D);
}
