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
        if (surface.SurfaceKind is < 1 or > 3 || (surface.RoleFlags & ~15u) != 0 ||
            surface.SurfaceKind == 1 && surface.RoleFlags != 0 ||
            surface.SurfaceKind == 2 && (surface.RoleFlags & 3u) != 1u ||
            surface.SurfaceKind == 3 && (surface.RoleFlags & 3u) != 3u)
            return "The engine surface kind and independent texture roles do not match the executable native schema.";
        if (!IsFinite(surface.BaseColorOpacity) || !IsFinite(surface.RoughnessMetallicSpecularEmission))
            return "Native engine surface factors must be finite.";
        for (int index = 0; index < AdvancedEngineSurfaceRecord.RoleCount; ++index)
        {
            if ((surface.RoleFlags & (1u << index)) == 0) continue;
            AdvancedEngineSurfaceTextureRole role = surface.GetRole(index);
            uint decodeFlags = index == 0 ? role.DecodeFlags & 1u : index == 1 ? 2u : 0u;
            if (!role.Binding.Texture.Handle.IsValid || !role.Binding.Sampler.Handle.IsValid ||
                role.UvScaleOffset != new Vector4(1, 1, 0, 0) || role.UvRotation != 0 ||
                role.TexCoordSet != 0 || role.Channel != 0 || role.DecodeFlags != decodeFlags)
                return "Native engine texture roles require current bindings, UV0, identity transforms, red scalar channels, and linear RGB normal/scalar semantics.";
        }
        return null;
    }

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
