using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>Selects exact cooked shadow replay without replacing the authored surface or its geometry.</summary>
internal static class CookedShadowMaterialResolver
{
    internal static ResolvedMeshRenderMaterial Resolve(XRMaterial? source, XRMaterial? shadowOverride)
    {
        if (shadowOverride is null)
            throw new NotSupportedException("WebGPU.ShadowCaster.OverrideUnsupported: a shadow-depth override is required.");
        bool point = shadowOverride.EngineSemantic == EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1;
        bool spot = shadowOverride.EngineSemantic == EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1;
        if ((!point && !spot && shadowOverride.EngineSemantic != EngineMaterialSemanticIdentity.OpaqueShadowDepthV1) ||
            shadowOverride.Shaders.Count != 0)
            throw new NotSupportedException("WebGPU.ShadowCaster.OverrideUnsupported: expected an exact source-free projected or radial shadow-depth override.");
        if (source?.EngineSemantic.IsColorCoverage() == true)
        {
            XRMaterial variant = (point ? source.GetPointShadowCasterVariant(EPointShadowMaterialKind.None)
                : spot ? source.GetStandardLitSpotShadowVariant() : source.ShadowCasterVariant)
                ?? throw new NotSupportedException("WebGPU.ShadowCaster.MaterialUnsupported: the coverage surface has no exact caster variant.");
            variant.ShadowUniformSourceMaterial = shadowOverride;
            return new(variant, shadowOverride, true, false, "CookedLitColorCoverageShadowDepth");
        }
        if (source?.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV1)
        {
            if (!EngineLitShadowCompanionContract.TryGetReceiverKey(source, RuntimeEngineMaterialArtifactServices.Resolver,
                localShadows: true, out _, out string reason))
                throw new NotSupportedException($"WebGPU.ShadowCaster.AuthoredSourceUnsupported: '{source.Name}': {reason}");
            return new(shadowOverride, null, true, false, "CookedGeneratedOpaqueShadowDepth");
        }
        if (source?.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitTextureV1)
        {
            if (source.Shaders.Count != 0 || !StandardLitTextureSurfaceBinding.TryRead(source, out _, out _))
                throw new NotSupportedException("WebGPU.ShadowCaster.TexturedSurfaceUnsupported: an exact source-free opaque textured surface is required.");
            return new(shadowOverride, null, true, false, "CookedOpaqueTexturedShadowDepth");
        }
        if (source?.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitColorV1 || source.Shaders.Count != 0 ||
            !source.CanUseSharedOpaqueShadowMaterial())
            throw new NotSupportedException("WebGPU.ShadowCaster.MaterialUnsupported: expected an exact opaque source without material-specific vertex behavior.");
        return new(shadowOverride, null, true, false, "CookedOpaqueShadowDepth");
    }
}
