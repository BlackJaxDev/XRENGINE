using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Checks the retained native engine surface without reading live material state.</summary>
public static class WebGpuAdvancedEngineSurfaceContract
{
    public static string? GetRejection(in AdvancedMaterialRecord material, AdvancedMaterialPublicationSnapshot payloads)
    {
        if (material.SourceContract is not (EAdvancedMaterialSourceContract.StandardSurface or EAdvancedMaterialSourceContract.EngineGeneratedSurface)) return null;
        if (material.CoverageMode is not (EAdvancedMaterialCoverageMode.Opaque or EAdvancedMaterialCoverageMode.Masked))
            return "Sorted transparency cannot enter native opaque engine-surface shading.";
        if (!payloads.TryGetEngineSurface(in material, out AdvancedEngineSurfaceRecord surface) ||
            surface.SchemaVersion != AdvancedEngineSurfaceRecord.CurrentSchemaVersion || surface.Generation != material.Generation)
            return "The engine material has no current generation-checked native surface companion.";
        bool unlit = surface.SurfaceKind is >= AdvancedEngineSurfaceRecord.UnlitColorKind and <= AdvancedEngineSurfaceRecord.UnlitTextureArraySliceKind;
        if (surface.SurfaceKind is < 1 or > AdvancedEngineSurfaceRecord.UnlitTextureArraySliceKind || (surface.RoleFlags & ~63u) != 0 ||
            surface.SurfaceKind == 1 && surface.RoleFlags != 0 ||
            surface.SurfaceKind == 2 && ((surface.RoleFlags & 3u) != 1u || (surface.RoleFlags & 48u) != 0) ||
            surface.SurfaceKind == 3 && ((surface.RoleFlags & 3u) != 3u || (surface.RoleFlags & 48u) != 0) ||
            surface.SurfaceKind == AdvancedEngineSurfaceRecord.TexturedAlphaKind &&
                (surface.RoleFlags != 17u || material.SourceContract != EAdvancedMaterialSourceContract.EngineGeneratedSurface ||
                 material.CoverageMode != EAdvancedMaterialCoverageMode.Masked || surface.BaseColorOpacity != Vector4.One) ||
            surface.SurfaceKind == AdvancedEngineSurfaceRecord.AuthoredTexturedKind &&
                ((surface.RoleFlags & 1u) == 0 || (surface.RoleFlags & 34u) == 0 || (surface.RoleFlags & 12u) != 0 ||
                 material.SourceContract != EAdvancedMaterialSourceContract.EngineGeneratedSurface || surface.BaseColorOpacity != Vector4.One ||
                 ((surface.RoleFlags & 16u) != 0) != (material.CoverageMode == EAdvancedMaterialCoverageMode.Masked)) ||
            unlit && (material.SourceContract != EAdvancedMaterialSourceContract.EngineGeneratedSurface ||
                surface.RoleFlags != (surface.SurfaceKind == AdvancedEngineSurfaceRecord.UnlitColorKind ? 0u : 1u) ||
                material.CoverageMode != (surface.SurfaceKind == AdvancedEngineSurfaceRecord.UnlitAlphaTextureKind
                    ? EAdvancedMaterialCoverageMode.Masked : EAdvancedMaterialCoverageMode.Opaque) ||
                surface.SurfaceKind != AdvancedEngineSurfaceRecord.UnlitColorKind && surface.BaseColorOpacity != Vector4.One ||
                surface.SurfaceKind == AdvancedEngineSurfaceRecord.UnlitAlphaTextureKind &&
                    (surface.RoughnessMetallicSpecularEmission.X is < 0 or > 1 ||
                     surface.RoughnessMetallicSpecularEmission.Y != 0 ||
                     surface.RoughnessMetallicSpecularEmission.Z != 0 ||
                     surface.RoughnessMetallicSpecularEmission.W != 0) ||
                surface.SurfaceKind != AdvancedEngineSurfaceRecord.UnlitAlphaTextureKind &&
                    surface.RoughnessMetallicSpecularEmission != Vector4.Zero))
            return "The engine surface kind and independent texture roles do not match the executable native schema.";
        if (!IsFinite(surface.BaseColorOpacity) || !IsFinite(surface.RoughnessMetallicSpecularEmission) ||
            !IsFinite(surface.NormalControls) || surface.NormalControls.Z != 0 || surface.NormalControls.W != 0 ||
            (surface.SurfaceKind != AdvancedEngineSurfaceRecord.AuthoredTexturedKind || (surface.RoleFlags & 2u) == 0
                ? surface.NormalControls != Vector4.Zero : surface.NormalControls.X is not (0 or 1)))
            return "Native engine surface factors must be finite.";
        if (surface.SamplingReserved0 != 0 || surface.SamplingReserved1 != 0)
            return "Native engine surface sampling padding must be zero.";
        for (int index = 0; index < AdvancedEngineSurfaceRecord.RoleCount; ++index)
        {
            uint sampling = surface.GetSamplingKey(index);
            if ((surface.RoleFlags & (1u << index)) == 0)
            {
                if (sampling != 0) return "An absent engine texture role must have no sampling identity.";
                continue;
            }
            if (!new AdvancedEngineSurfaceSamplingKey(sampling).IsValid)
                return "Native engine texture roles require valid frozen mip views and relative LOD clamps.";
            AdvancedEngineSurfaceTextureRole role = surface.GetRole(index);
            uint decodeFlags = index == 0 ? role.DecodeFlags & 1u : index == 1 ? 2u : 0u;
            if (!role.Binding.Texture.Handle.IsValid || !role.Binding.Sampler.Handle.IsValid ||
                role.UvScaleOffset != new Vector4(1, 1, 0, 0) || role.UvRotation != 0 ||
                role.TexCoordSet != 0 || role.Channel != 0 || role.DecodeFlags != decodeFlags)
                return "Native engine texture roles require current bindings, UV0, identity transforms, red scalar channels, and explicit linear normal/height/scalar semantics.";
        }
        return null;
    }

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
