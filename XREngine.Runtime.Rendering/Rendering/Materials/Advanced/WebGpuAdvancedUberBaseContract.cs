namespace XREngine.Rendering;

/// <summary>Validates the retained independent Uber row before native shader indexing or resource collection.</summary>
public static class WebGpuAdvancedUberBaseContract
{
    public static string? GetRejection(in AdvancedMaterialRecord material, AdvancedMaterialPublicationSnapshot payloads)
    {
        if (material.SourceContract != EAdvancedMaterialSourceContract.UberBaseSurface) return null;
        if (!payloads.TryGetUberBaseSurface(in material, out AdvancedUberBaseSurfaceRecord surface) ||
            surface.Generation != material.Generation || surface.SchemaVersion != 1 || (surface.PipelineFlags & ~15u) != 0 || surface.SamplingReserved != 0 ||
            (surface.Features & ~UberBaseMaterialProfile.SupportedFeatures) != 0)
            return "The material has no current generation-checked Uber schema1 companion.";
        int mode = unchecked((int)surface.GetParameterWord(112 / 4));
        if (mode is not (0 or 1) || (mode == 1) != (material.CoverageMode == EAdvancedMaterialCoverageMode.Masked))
            return "Native Uber coverage must preserve canonical opaque or cutout mode and its matching material pass.";
        if (GetParameterRejection(in surface) is { } parameterReason) return parameterReason;
        for (int role = 0; role < AdvancedUberBaseSurfaceRecord.RoleCount; role++)
        {
            bool active = UberBaseMaterialProfile.IsRoleActive(role, surface.Features);
            AdvancedMaterialTextureBinding binding = surface.GetBinding(role);
            uint sampling = surface.GetSamplingKey(role);
            if (active ? !binding.Texture.Handle.IsValid || !binding.Sampler.Handle.IsValid || !new AdvancedEngineSurfaceSamplingKey(sampling).IsValid
                : binding.Texture.Handle.IsValid || binding.Sampler.Handle.IsValid || sampling != 0)
                return "Every active Uber texture role requires its own current image/sampler and exact mip-view identity.";
        }
        return null;
    }

    // Source selectors, including reflected and polar coordinates, execute in the shared raster producer.
    public static string? GetParameterRejection(in AdvancedUberBaseSurfaceRecord surface) => null;
}
