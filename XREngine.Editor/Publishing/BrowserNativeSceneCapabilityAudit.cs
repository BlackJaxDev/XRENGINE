using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Components;
using XREngine.Components.Capture;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Core;

namespace XREngine.Editor.Publishing;

/// <summary>Captures cold source checks before temporary worlds retire, then admits the complete startup and streamed scene set.</summary>
internal sealed class BrowserNativeSceneCapabilityAudit(GameStartupSettings? startup = null)
{
    private readonly HashSet<RenderPipelineRequirements> _requirements = [];
    private readonly Dictionary<XRMesh, (string? Geometry, string? Deformation, string? Meshlets)> _geometry = new(ReferenceEqualityComparer.Instance);
    private readonly List<BrowserNativeScenePassAdmission> _passes = [];
    private readonly List<BrowserNativeResourceAdmission> _globals = [];
    private readonly EMeshSubmissionStrategy _startupStrategy = ResolveStartupStrategy(startup);

    internal void IncludeRequirements(IEnumerable<RenderPipelineRequirements> requirements)
        => _requirements.UnionWith(requirements);

    internal void InspectGeometry(XRMesh? mesh, XRMaterial material, string scenePath, string path, string? meshName,
        CancellationToken cancellationToken)
    {
        (string? Geometry, string? Deformation, string? Meshlets) geometry;
        if (mesh is null)
            geometry = ("The selected native draw has no mesh source.", null, null);
        else if (!_geometry.TryGetValue(mesh, out geometry))
        {
            bool valid = AdvancedGpuScenePublisher.TryInspectCanonicalGeometry(mesh, out string geometryReason);
            string? deformationReason = null;
            if (valid && !AdvancedGpuDeformationResources.TryInspectNativeMesh(mesh, out string reason, cancellationToken))
                deformationReason = reason;
            bool meshlets = AdvancedGpuScenePublisher.TryInspectCanonicalMeshlets(mesh, out string meshletReason);
            geometry = (valid ? null : geometryReason, deformationReason, meshlets ? null : meshletReason);
            _geometry.Add(mesh, geometry);
        }
        InspectPass(material.RenderPass, material.RenderOptions, "base", null);
        foreach (MaterialPassDefinition pass in material.PassSet.Passes)
            if (pass.Enabled)
                InspectPass(pass.RenderPass, pass.RenderOptions, pass.SourcePassName ?? pass.Identity.ToString(), pass.VertexShaderPath);

        void InspectPass(int pass, RenderingParameters options, string source, string? vertexShader)
        {
            if (pass == (int)EDefaultRenderPass.Background) return;
            cancellationToken.ThrowIfCancellationRequested();
            string resource = "native-vertex-program";
            string reason = "The authored pass vertex stage requires an exact native vertex companion.";
            string kernel = string.Empty;
            AdvancedGpuResourceBindingSource[] pairs = [];
            bool admitted = string.IsNullOrWhiteSpace(vertexShader) && WebGpuAdvancedSceneAdmission.TryInspectMaterial(
                material, pass, options, out kernel, out pairs, out resource, out reason);
            BrowserNativeResourceAdmission[] frozenPairs = pairs.Select(pair => new BrowserNativeResourceAdmission(
                scenePath, path, $"material-texture/{pair.Texture?.Name ?? "<unnamed>"}", pair.Texture!,
                pair.TextureRecord, pair.SamplerRecord, false)).ToArray();
            _passes.Add(new(scenePath, path, meshName, material.Name, pass, source, geometry.Geometry,
                geometry.Deformation, geometry.Meshlets, admitted ? null : reason, resource, kernel, frozenPairs));
        }
    }

    internal void InspectGlobalResources(XRComponent component, string scenePath, string path)
    {
        if (component is LightComponent { Type: ELightType.Dynamic, CastsShadows: true } light)
        {
            bool valid = WebGpuStandaloneShadowResourceContract.TryDescribe(light, out AdvancedTextureRecord texture,
                out AdvancedSamplerRecord sampler, out _, out string reason);
            EAdvancedShadowType? type = light switch
            {
                DirectionalLightComponent => EAdvancedShadowType.DirectionalCascade,
                PointLightComponent => EAdvancedShadowType.PointCube,
                SpotLightComponent => EAdvancedShadowType.Spot,
                _ => null,
            };
            _globals.Add(new(scenePath, path, $"standalone-shadow/{light.Name ?? light.GetType().Name}", light,
                texture, sampler, false, valid ? null : reason, type));
        }
        if (component is DeferredDecalComponent decal)
            _globals.Add(new(scenePath, path, $"decal/{decal.Material?.Name ?? "<unbound>"}", decal, default, default, false,
                "The authored decal has no canonical native decal publication owner; a raster decal material cannot establish an enabled native decal and its exact mask/material resource closure."));
        if (component is not LightProbeComponent probe) return;
        if (!probe.TryGetActiveIblOutput(out LightProbeIblOutputGeneration generation))
        {
            _globals.Add(new(scenePath, path, "probe-ibl-publication", probe, default, default, true,
                "The selected probe has no exact cooked active irradiance/prefilter generation; runtime capture textures cannot be inferred or treated as already rendered during cold publication."));
            return;
        }
        InspectProbeTexture(generation.Irradiance, "probe-irradiance");
        InspectProbeTexture(generation.PrefilteredRadiance, "probe-prefiltered-radiance");

        void InspectProbeTexture(XRTexture texture, string resource)
        {
            bool valid = AdvancedGpuResourceSourceEncoder.TryEncode(texture, EAdvancedResourceFallback.Zero,
                out AdvancedGpuResourceBindingSource source, out _, out string reason) &&
                WebGpuAdvancedSceneAdmission.TryInspectTexture(in source, false, out reason);
            _globals.Add(new(scenePath, path, $"{resource}/{texture.Name ?? "<unnamed>"}", texture,
                source.TextureRecord, source.SamplerRecord, true, valid ? null : reason));
        }
    }

    internal void Complete(BrowserCapabilityReport? report, CancellationToken cancellationToken)
    {
        foreach (RenderPipelineRequirements requirements in _requirements)
        {
            if (!requirements.RequiresNativeScenePasses && requirements.NativeScenePasses.Count == 0) continue;
            List<BrowserNativeResourceAdmission> globals = [];
            Dictionary<EAdvancedShadowType, int> shadows = [];
            foreach (BrowserNativeResourceAdmission global in _globals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (global.Probe && !requirements.NativeProbeIbl) continue;
                if (global.Reason is { } globalReason)
                {
                    RejectGlobal(global, globalReason);
                    continue;
                }
                if (global.ShadowType is { } type)
                {
                    int count = shadows.GetValueOrDefault(type) + 1;
                    shadows[type] = count;
                    if (count > 1)
                        RejectGlobal(global, $"The complete startup and streamed native scene set selects {count} '{type}' receivers; only one standalone receiver of each shadow type is supported.");
                }
                AddPair(globals, global);
                if (GetBankRejection(globals) is { } globalCapacity)
                    RejectGlobal(global, globalCapacity);
            }
            List<(string Kernel, BrowserNativeResourceAdmission[] Pairs)> cohorts = [];
            foreach (BrowserNativeScenePassAdmission entry in _passes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!requirements.NativeScenePasses.TryGetValue(entry.Pass, out EMeshSubmissionStrategy? authoredStrategy)) continue;
                EMeshSubmissionStrategy strategy = authoredStrategy ?? _startupStrategy;
                if (!Enum.IsDefined(strategy)) Reject(entry, "native-mesh-submission", $"The authored strategy '{strategy}' is not a supported mesh submission selection.");
                if (entry.GeometryReason is { } geometry) Reject(entry, "canonical-geometry", geometry);
                if (entry.DeformationReason is { } deformation) Reject(entry, "aggregate-deformation", deformation);
                if (strategy.IsAnyMeshletStrategy() && entry.MeshletReason is { } meshlets)
                    Reject(entry, $"native-meshlet-payload/{strategy}", meshlets);
                if (entry.MaterialReason is { } material)
                {
                    Reject(entry, entry.Resource, material);
                    continue;
                }
                List<BrowserNativeResourceAdmission> pairs = [.. globals];
                foreach (BrowserNativeResourceAdmission source in entry.Pairs)
                    AddPair(pairs, source);
                if (GetBankRejection(pairs) is { } capacity)
                {
                    Reject(entry, $"native-texture-bank/{string.Join(", ", pairs.Select(pair => pair.Resource))}", capacity);
                    continue;
                }
                if (cohorts.Any(cohort => cohort.Kernel == entry.Kernel && SamePairs(cohort.Pairs, pairs))) continue;
                if (cohorts.Count >= WebGpuAdvancedMaterialContract.MaximumMaterialCohorts)
                {
                    Reject(entry, "native-material-cohorts", "The selected native scene exceeds 127 material cohorts plus the diagnostic cohort.");
                    continue;
                }
                cohorts.Add((entry.Kernel, [.. pairs]));
            }
        }
        // Only immutable checks and exact resource identity comparisons outlive each temporary cook world.
        _geometry.Clear();

        void RejectGlobal(BrowserNativeResourceAdmission entry, string reason)
        {
            NotSupportedException error = new($"BrowserCook.NativeGlobalResourceUnsupported: '{entry.Path}', pass 'native-opaque-shading', resource '{entry.Resource}': {reason}");
            if (report is null) throw error;
            report.Inspect(() => throw error, entry.ScenePath, entry.Path, pass: "native-opaque-shading");
        }

        void Reject(BrowserNativeScenePassAdmission entry, string resource, string reason)
        {
            NotSupportedException error = new($"BrowserCook.NativeSceneUnsupported: '{entry.Path}', mesh '{entry.MeshName}', material '{entry.MaterialName}', pass '{entry.Pass}', source pass '{entry.Source}', resource '{resource}': {reason}");
            if (report is null) throw error;
            report.Inspect(() => throw error, entry.ScenePath, entry.Path, material: entry.MaterialName,
                pass: entry.Pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static bool SamePairs(IReadOnlyList<BrowserNativeResourceAdmission> left, IReadOnlyList<BrowserNativeResourceAdmission> right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
            if (!ReferenceEquals(left[index].Identity, right[index].Identity) ||
                !left[index].Sampler.Equals(right[index].Sampler)) return false;
        return true;
    }

    private static void AddPair(List<BrowserNativeResourceAdmission> pairs, BrowserNativeResourceAdmission source)
    {
        if (!pairs.Any(pair => ReferenceEquals(pair.Identity, source.Identity) && pair.Sampler.Equals(source.Sampler)))
            pairs.Add(source);
    }

    private static string? GetBankRejection(List<BrowserNativeResourceAdmission> pairs)
    {
        int color2D = 0, depth2D = 0, cube = 0, array = 0;
        foreach (BrowserNativeResourceAdmission pair in pairs)
            switch (pair.Texture.Dimension)
            {
                case EAdvancedTextureDimension.Texture2D:
                    if ((pair.Texture.Flags & EAdvancedTextureRecordFlags.Depth) != 0) depth2D++;
                    else color2D++;
                    break;
                case EAdvancedTextureDimension.Cube: cube++; break;
                case EAdvancedTextureDimension.Texture2DArray: array++; break;
                default: return $"Resource '{pair.Resource}' has no native sampled dimension companion.";
            }
        return WebGpuAdvancedMaterialContract.GetTextureBankRejection(color2D, depth2D, cube, array);
    }

    private static EMeshSubmissionStrategy ResolveStartupStrategy(GameStartupSettings? settings)
    {
        if (settings is null) return EMeshSubmissionStrategy.CpuDirect;
        UserSettings user = settings.DefaultUserSettings;
        bool gpu = user.GPURenderDispatchOverride.HasOverride ? user.GPURenderDispatchOverride.Value : settings.GPURenderDispatch;
        if (!gpu) return EMeshSubmissionStrategy.CpuDirect;
        bool zeroReadback = OverrideableSettingExtensions.ResolveValueCascade(false,
            settings.EnableZeroReadbackMaterialScatterOverride, user.EnableZeroReadbackMaterialScatterOverride);
        return zeroReadback ? EMeshSubmissionStrategy.GpuIndirectZeroReadback : EMeshSubmissionStrategy.GpuIndirectInstrumented;
    }
}
