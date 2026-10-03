using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.Publishing;

/// <summary>Captures cold source checks before temporary worlds retire, then admits the complete startup and streamed scene set.</summary>
internal sealed class BrowserNativeSceneCapabilityAudit
{
    private readonly HashSet<RenderPipelineRequirements> _requirements = [];
    private readonly Dictionary<XRMesh, (string? Geometry, string? Deformation)> _geometry = new(ReferenceEqualityComparer.Instance);
    private readonly List<BrowserNativeScenePassAdmission> _passes = [];

    internal void IncludeRequirements(IEnumerable<RenderPipelineRequirements> requirements)
        => _requirements.UnionWith(requirements);

    internal void InspectGeometry(XRMesh? mesh, XRMaterial material, string scenePath, string path, string? meshName,
        CancellationToken cancellationToken)
    {
        (string? Geometry, string? Deformation) geometry;
        if (mesh is null)
            geometry = ("The selected native draw has no mesh source.", null);
        else if (!_geometry.TryGetValue(mesh, out geometry))
        {
            bool valid = AdvancedGpuScenePublisher.TryInspectCanonicalGeometry(mesh, out string geometryReason);
            string? deformationReason = null;
            if (valid && !AdvancedGpuDeformationResources.TryInspectNativeMesh(mesh, out string reason, cancellationToken))
                deformationReason = reason;
            geometry = (valid ? null : geometryReason, deformationReason);
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
            _passes.Add(new(scenePath, path, meshName, material.Name, pass, source, geometry.Geometry,
                geometry.Deformation, admitted ? null : reason, resource, kernel, pairs));
        }
    }

    internal void Complete(BrowserCapabilityReport? report, CancellationToken cancellationToken)
    {
        foreach (RenderPipelineRequirements requirements in _requirements)
        {
            if (!requirements.Operations.Contains("advanced-stage-execution")) continue;
            List<(string Kernel, AdvancedGpuResourceBindingSource[] Pairs)> cohorts = [];
            foreach (BrowserNativeScenePassAdmission entry in _passes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!requirements.ScenePasses.Contains(entry.Pass)) continue;
                if (entry.GeometryReason is { } geometry) Reject(entry, "canonical-geometry", geometry);
                if (entry.DeformationReason is { } deformation) Reject(entry, "aggregate-deformation", deformation);
                if (entry.MaterialReason is { } material)
                {
                    Reject(entry, entry.Resource, material);
                    continue;
                }
                if (cohorts.Any(cohort => cohort.Kernel == entry.Kernel && SamePairs(cohort.Pairs, entry.Pairs))) continue;
                if (cohorts.Count >= WebGpuAdvancedMaterialContract.MaximumMaterialCohorts)
                {
                    Reject(entry, "native-material-cohorts", "The selected native scene exceeds 127 material cohorts plus the diagnostic cohort.");
                    continue;
                }
                cohorts.Add((entry.Kernel, entry.Pairs));
            }
        }
        // Only immutable checks and exact resource identity comparisons outlive each temporary cook world.
        _geometry.Clear();

        void Reject(BrowserNativeScenePassAdmission entry, string resource, string reason)
        {
            NotSupportedException error = new($"BrowserCook.NativeSceneUnsupported: '{entry.Path}', mesh '{entry.MeshName}', material '{entry.MaterialName}', pass '{entry.Pass}', source pass '{entry.Source}', resource '{resource}': {reason}");
            if (report is null) throw error;
            report.Inspect(() => throw error, entry.ScenePath, entry.Path, material: entry.MaterialName,
                pass: entry.Pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static bool SamePairs(AdvancedGpuResourceBindingSource[] left, AdvancedGpuResourceBindingSource[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (!ReferenceEquals(left[index].Texture, right[index].Texture) ||
                !left[index].SamplerRecord.Equals(right[index].SamplerRecord)) return false;
        return true;
    }
}
