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
    private bool _litV1, _litV2, _authoredLit;
    private bool _litTexture, _litNormalTexture;
    private readonly List<string> _lightPaths = [];
    private readonly Dictionary<EngineMaterialSemanticIdentity, (string ScenePath, string Path, string? Material, string? Mesh)> _materialPaths = [];

    internal void InspectMaterial(XRMaterial? material, string path, string? mesh, string? scenePath = null)
    {
        if (material is not null)
            _materialPaths.TryAdd(material.EngineSemantic, (scenePath ?? string.Empty, path, material.Name, mesh));
        _litV1 |= material?.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV1;
        _litV2 |= material?.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV2;
        _authoredLit |= material?.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV1;
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
        _lightPaths.Add($"'{path}' ({light.GetType().Name})");
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
                    directional.ShadowMapResolutionWidth == 0 || directional.ShadowMapResolutionHeight == 0)
                    throw new NotSupportedException($"BrowserCook.ShadowProfileUnsupported: '{path}' requires standalone non-cascaded directional depth, PCSS 8/8, no contact shadows, and positive authored dimensions.");
                break;
            case PointLightComponent point:
                _pointLights++;
                if (!point.CastsShadows) break;
                _pointShadows++;
                try { point.ValidateCookedShadowConfiguration(); }
                catch (NotSupportedException error)
                {
                    throw new NotSupportedException($"BrowserCook.ShadowProfileUnsupported: '{path}', pass 'point-shadow-depth': {error.Message}", error);
                }
                break;
            case SpotLightComponent spot:
                _spotLights++;
                if (!spot.CastsShadows) break;
                _spotShadows++;
                try { spot.ValidateCookedShadowConfiguration(); }
                catch (NotSupportedException error)
                {
                    throw new NotSupportedException($"BrowserCook.ShadowProfileUnsupported: '{path}', pass 'spot-shadow-depth': {error.Message}", error);
                }
                break;
        }
    }

    internal void Complete(BrowserCapabilityReport? report = null, string? worldPath = null)
    {
        if (_directionalLights > 4 || _pointLights > 8 || _spotLights > 8 ||
            _directionalShadows > 1 || _pointShadows > 1 || _spotShadows > 1)
            Collect(() => throw new NotSupportedException($"BrowserCook.LightCapacityExceeded: pass 'forward-lighting' supports 4 directional, 8 point, 8 spot lights and at most one standalone shadow map for each type. Authored lights: {string.Join(", ", _lightPaths)}."),
                null, "forward-lighting");
        bool local = _pointShadows != 0 || _spotShadows != 0;
        if (_directionalShadows == 0 && !local) return;
        if (_authoredLit)
        {
            var authored = _materialPaths[EngineMaterialSemanticIdentity.AuthoredLitV1];
            Collect(() => throw new NotSupportedException($"BrowserCook.AuthoredLitShadowUnsupported: '{authored.Path}' mesh '{authored.Mesh}', material '{authored.Material}' needs an authored cooked shadow-caster companion before using casting lights."),
                EngineMaterialSemanticIdentity.AuthoredLitV1, "shadow-caster");
        }
        string output = local ? "linear-hdr-local-shadows-v1" : "linear-hdr-directional-shadow-v1";
        if (_litV1) Check(EngineMaterialSemanticIdentity.StandardLitColorV1, "opaque-forward", "static-position-normal-v1", output);
        if (_litV2) Check(EngineMaterialSemanticIdentity.StandardLitColorV2, "forward-coverage", "static-position-normal-v1", output);
        if (_litTexture) Check(EngineMaterialSemanticIdentity.StandardLitTextureV1, "opaque-forward", "position-normal-uv-v1", output);
        if (_litNormalTexture) Check(EngineMaterialSemanticIdentity.StandardLitTextureV1, "opaque-forward", "position-normal-tangent-uv-v1", output);
        if (_directionalShadows != 0)
        {
            Check(EngineMaterialSemanticIdentity.OpaqueShadowDepthV1, "depth", "static-position-v1", "depth-normal-v1");
            if (_litV2) Check(EngineMaterialSemanticIdentity.StandardLitColorV2, "depth", "static-position-v1", "depth-normal-v1");
        }
        if (_pointShadows != 0)
        {
            Check(EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1, "point-shadow-depth", "static-position-v1", "radial-r16f-v1");
            if (_litV2) Check(EngineMaterialSemanticIdentity.StandardLitColorV2, "point-shadow-depth", "static-position-v1", "radial-r16f-v1");
        }
        if (_spotShadows != 0)
        {
            Check(EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1, "spot-shadow-depth", "static-position-v1", "projected-r16f-v1");
            if (_litV2) Check(EngineMaterialSemanticIdentity.StandardLitColorV2, "spot-shadow-depth", "static-position-v1", "projected-r16f-v1");
        }

        void Check(EngineMaterialSemanticIdentity semantic, string pass, string vertex, string target)
            => Collect(() => Require(semantic, pass, vertex, target), semantic, pass);

        void Collect(Action action, EngineMaterialSemanticIdentity? semantic, string pass)
        {
            if (report is null)
            {
                action();
                return;
            }
            var location = semantic is not null && _materialPaths.TryGetValue(semantic.Value, out var material)
                ? material : (ScenePath: worldPath ?? string.Empty, Path: "lighting", Material: (string?)null, Mesh: (string?)null);
            report.Inspect(action, location.ScenePath, location.Path, material: location.Material, pass: pass);
        }
    }

    private void Require(EngineMaterialSemanticIdentity semantic, string pass, string vertex, string output)
    {
        EngineMaterialVariantKey key = new(semantic, ShaderCompileTarget.WebGPUWgsl, pass, vertex, output);
        if (resolver is BrowserShaderArtifactSource source)
            foreach (EngineMaterialVariantEntry entry in source.MaterialVariants)
                if (entry.Key == key) return;
        if (_materialPaths.TryGetValue(semantic, out var sourceMaterial))
            throw new NotSupportedException($"BrowserCook.ShadowVariantMissing: '{sourceMaterial.Path}' mesh '{sourceMaterial.Mesh}', material '{sourceMaterial.Material}', pass '{pass}' requires exact variant '{key}'.");
        throw new NotSupportedException($"BrowserCook.ShadowVariantMissing: pass '{pass}' requires exact variant '{key}' for authored lights: {string.Join(", ", _lightPaths)}.");
    }
}
