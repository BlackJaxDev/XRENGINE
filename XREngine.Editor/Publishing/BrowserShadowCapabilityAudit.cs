using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor.Publishing;

/// <summary>Checks the bounded forward light/caster contract before publishing a shared world.</summary>
internal sealed class BrowserShadowCapabilityAudit(IShaderProgramArtifactResolver? resolver)
{
    private int _directionalLights, _pointLights, _spotLights;
    private int _directionalShadows, _pointShadows, _spotShadows;
    private bool _litV1, _litV2;
    private bool _litTexture, _litNormalTexture;

    internal void InspectMaterial(XRMaterial? material)
    {
        _litV1 |= material?.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV1;
        _litV2 |= material?.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV2;
        if (material?.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitTextureV1)
        {
            if (material.GetSurfaceTexture(XREngine.Rendering.Materials.EMaterialTextureSemantic.Normal) is null)
                _litTexture = true;
            else
                _litNormalTexture = true;
        }
    }

    internal void Inspect(XRComponent component, string path)
    {
        if (component is not LightComponent { Type: ELightType.Dynamic } light) return;
        switch (light)
        {
            case DirectionalLightComponent directional:
                _directionalLights++;
                if (!directional.CastsShadows) break;
                _directionalShadows++;
                if (directional.UseShadowAtlas || directional.EnableCascadedShadows ||
                    directional.ShadowMapEncoding != EShadowMapEncoding.Depth || directional.EnableContactShadows ||
                    directional.ShadowMapStorageFormat is not (EShadowMapStorageFormat.Depth16 or EShadowMapStorageFormat.Depth24 or EShadowMapStorageFormat.Depth32Float) ||
                    directional.SoftShadowMode != ESoftShadowMode.ContactHardeningPcss ||
                    directional.BlockerSamples != 8 || directional.FilterSamples != 8 ||
                    directional.ShadowMapResolutionWidth is 0 or > 2048 || directional.ShadowMapResolutionHeight is 0 or > 2048)
                    throw new NotSupportedException($"BrowserCook.ShadowProfileUnsupported: '{path}' requires standalone non-cascaded directional depth, PCSS 8/8, no contact shadows, and dimensions at most 2048.");
                break;
            case PointLightComponent point:
                _pointLights++;
                if (!point.CastsShadows) break;
                _pointShadows++;
                point.ValidateCookedShadowConfiguration();
                break;
            case SpotLightComponent spot:
                _spotLights++;
                if (!spot.CastsShadows) break;
                _spotShadows++;
                spot.ValidateCookedShadowConfiguration();
                break;
        }
    }

    internal void Complete()
    {
        if (_directionalLights > 4 || _pointLights > 8 || _spotLights > 8 ||
            _directionalShadows > 1 || _pointShadows > 1 || _spotShadows > 1)
            throw new NotSupportedException("BrowserCook.LightCapacityExceeded: the forward profile supports 4 directional, 8 point, 8 spot lights and at most one standalone shadow map for each type.");
        bool local = _pointShadows != 0 || _spotShadows != 0;
        if (_directionalShadows == 0 && !local) return;
        string output = local ? "linear-hdr-local-shadows-v1" : "linear-hdr-directional-shadow-v1";
        if (_litV1) Require(EngineMaterialSemanticIdentity.StandardLitColorV1, "opaque-forward", "static-position-normal-v1", output);
        if (_litV2) Require(EngineMaterialSemanticIdentity.StandardLitColorV2, "forward-coverage", "static-position-normal-v1", output);
        if (_litTexture) Require(EngineMaterialSemanticIdentity.StandardLitTextureV1, "opaque-forward", "position-normal-uv-v1", output);
        if (_litNormalTexture) Require(EngineMaterialSemanticIdentity.StandardLitTextureV1, "opaque-forward", "position-normal-tangent-uv-v1", output);
        if (_directionalShadows != 0)
        {
            Require(EngineMaterialSemanticIdentity.OpaqueShadowDepthV1, "depth", "static-position-v1", "depth-normal-v1");
            if (_litV2) Require(EngineMaterialSemanticIdentity.StandardLitColorV2, "depth", "static-position-v1", "depth-normal-v1");
        }
        if (_pointShadows != 0)
        {
            Require(EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1, "point-shadow-depth", "static-position-v1", "radial-r16f-v1");
            if (_litV2) Require(EngineMaterialSemanticIdentity.StandardLitColorV2, "point-shadow-depth", "static-position-v1", "radial-r16f-v1");
        }
        if (_spotShadows != 0)
        {
            Require(EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1, "spot-shadow-depth", "static-position-v1", "projected-r16f-v1");
            if (_litV2) Require(EngineMaterialSemanticIdentity.StandardLitColorV2, "spot-shadow-depth", "static-position-v1", "projected-r16f-v1");
        }
    }

    private void Require(EngineMaterialSemanticIdentity semantic, string pass, string vertex, string output)
    {
        EngineMaterialVariantKey key = new(semantic, ShaderCompileTarget.WebGPUWgsl, pass, vertex, output);
        if (resolver is BrowserShaderArtifactSource source)
            foreach (EngineMaterialVariantEntry entry in source.MaterialVariants)
                if (entry.Key == key) return;
        throw new NotSupportedException($"BrowserCook.ShadowVariantMissing: the shared world requires exact variant '{key}'.");
    }
}
