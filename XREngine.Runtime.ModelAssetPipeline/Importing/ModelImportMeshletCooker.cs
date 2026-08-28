using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Caching;
using System.Text;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene;

namespace XREngine.Rendering.Models;

/// <summary>
/// Owns deterministic import-time LOD and meshlet cooking. It is intentionally
/// invoked after import normalization and before asset externalization so render
/// code only ever consumes already cooked payloads.
/// </summary>
public static class ModelImportMeshletCooker
{
    /// <summary>
    /// Cooks every renderable submesh reachable from an imported scene root.
    /// This is the entry point used by direct <see cref="XRPrefabSource"/>
    /// imports as well as the asset-manager publication path.
    /// </summary>
    public static ModelImportMeshletCookResult CookScene(
        SceneNode root,
        ModelCookSettings modelDefaults,
        string stableModelIdentity,
        ModelCookOverrideSnapshot? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        List<SubMesh> subMeshes = [];
        CollectSubMeshes(root, parentPath: "fallback", siblingIndex: 0, subMeshes);
        return Cook(subMeshes, modelDefaults, stableModelIdentity, overrides);
    }

    public static ModelImportMeshletCookResult Cook(
        IEnumerable<SubMesh> subMeshes,
        ModelCookSettings modelDefaults,
        string stableModelIdentity,
        ModelCookOverrideSnapshot? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(subMeshes);
        ArgumentNullException.ThrowIfNull(modelDefaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableModelIdentity);

        List<SubMesh> ordered = [.. subMeshes
            .Where(static subMesh => subMesh is not null)
            .Distinct()
            .OrderBy(GetCanonicalSubMeshKey, StringComparer.Ordinal)];
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        System.Diagnostics.Stopwatch cookStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int meshletCount = 0;
        int payloadCount = 0;
        int generatedLodCount = 0;

        for (int subMeshIndex = 0; subMeshIndex < ordered.Count; subMeshIndex++)
        {
            SubMesh subMesh = ordered[subMeshIndex];
            ApplyModelDefaultsWhenUnspecified(subMesh, modelDefaults);
            string localEntityIdentity = string.IsNullOrWhiteSpace(subMesh.ImportedEntityIdentity)
                ? $"fallback:{stableModelIdentity}/submesh:{subMeshIndex}"
                : subMesh.ImportedEntityIdentity;
            ModelCookOverrideEntry? overrideEntry = overrides?.Entries.FirstOrDefault(x => string.Equals(x.EntityKey.Value, localEntityIdentity, StringComparison.Ordinal));
            if (overrideEntry is not null)
                subMesh.MeshOptimizer = Clone(overrideEntry.Settings, useModelImportDefaults: false);

            string ownerIdentity = $"{stableModelIdentity}/{localEntityIdentity}";
            AssignStableLodKeys(subMesh, ownerIdentity, subMeshIndex);

            // Topology-changing LOD simplification must complete before meshlet
            // construction. Regeneration also removes stale auto LODs first.
            generatedLodCount += MeshOptimizerIntegration.RegenerateAutoLods(subMesh).Count;
            AssignStableLodKeys(subMesh, ownerIdentity, subMeshIndex);

            int lodIndex = 0;
            foreach (SubMeshLOD lod in subMesh.LODs)
            {
                XRMesh? mesh = lod.Mesh;
                if (mesh is null)
                {
                    lodIndex++;
                    continue;
                }

                ImportedEntityKey entityKey = new(
                    $"{ownerIdentity}/lod:{lodIndex}",
                    isStable: subMesh.ImportedEntityIdentityIsStable);
                MeshletPayload payload = mesh.GetOrCreateMeshletPayload(
                    subMesh.MeshOptimizer.Meshlets,
                    subMesh.MeshOptimizer.Lods,
                    entityKey.Value);
                payload.ValidatePortablePayload();
                payloadCount++;
                meshletCount += payload.Meshlets.Length;
                lodIndex++;
            }
        }

        cookStopwatch.Stop();
        RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordMeshletColdImport(
            buildTime: cookStopwatch.Elapsed,
            allocatedBytes: Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - allocationStart),
            generatedLods: generatedLodCount,
            payloads: payloadCount,
            meshlets: meshletCount);
        return new ModelImportMeshletCookResult(ordered.Count, generatedLodCount, payloadCount, meshletCount);
    }

    private static void ApplyModelDefaultsWhenUnspecified(SubMesh subMesh, ModelCookSettings defaults)
    {
        MeshOptimizerSubMeshSettings settings = subMesh.MeshOptimizer;
        if (!settings.UseModelImportDefaults)
            return;

        // Imported submeshes begin with generic (disabled) settings. Materialize
        // the effective import policy onto each submesh so it is serializable and
        // cache fingerprints see exactly the settings used to cook it.
        settings.Meshlets = Clone(defaults.Meshlets);
        settings.Lods = Clone(defaults.Lods);
    }

    private static string GetCanonicalSubMeshKey(SubMesh subMesh)
    {
        XRMesh? baseMesh = subMesh.LODs.FirstOrDefault(static lod => lod.Mesh is not null)?.Mesh;
        ulong geometryHash = baseMesh is null ? 0UL : MeshletPayloadUtility.ComputeSourceMeshHash(baseMesh);
        string name = (subMesh.Name ?? string.Empty).Normalize(NormalizationForm.FormC);
        return $"{name}:{geometryHash:X16}:{subMesh.LODs.Count}";
    }

    private static void AssignStableLodKeys(SubMesh subMesh, string stableModelIdentity, int subMeshIndex)
    {
        List<SubMeshLOD> lods = [.. subMesh.LODs.OrderBy(static lod => lod.MaxVisibleDistance)];
        for (int lodIndex = 0; lodIndex < lods.Count; lodIndex++)
            lods[lodIndex].StableSortKey = $"{stableModelIdentity}/submesh:{subMeshIndex}/lod:{lodIndex}";

        subMesh.LODs = new SortedSet<SubMeshLOD>(lods, new LODSorter());
    }

    private static void CollectSubMeshes(
        SceneNode node,
        string parentPath,
        int siblingIndex,
        ICollection<SubMesh> destination)
    {
        string nodeName = string.IsNullOrWhiteSpace(node.Name) ? "Node" : node.Name;
        string nodePath = $"{parentPath}/{EscapeKeySegment(nodeName)}[{siblingIndex}]";
        ModelComponent[] modelComponents = node.GetComponents<ModelComponent>().ToArray();
        for (int modelIndex = 0; modelIndex < modelComponents.Length; modelIndex++)
        {
            ModelComponent component = modelComponents[modelIndex];
            if (component.Model is { } model)
                for (int subMeshIndex = 0; subMeshIndex < model.Meshes.Count; subMeshIndex++)
                {
                    SubMesh subMesh = model.Meshes[subMeshIndex];
                    if (string.IsNullOrWhiteSpace(subMesh.ImportedEntityIdentity))
                    {
                        subMesh.ImportedEntityIdentity = $"{nodePath}:model:{modelIndex}:submesh:{subMeshIndex}";
                        subMesh.ImportedEntityIdentityIsStable = false;
                    }
                    destination.Add(subMesh);
                }
        }

        for (int childIndex = 0; childIndex < node.Transform.Children.Count; childIndex++)
            if (node.Transform.Children[childIndex]?.SceneNode is SceneNode child)
                CollectSubMeshes(child, nodePath, childIndex, destination);
    }

    private static string EscapeKeySegment(string value)
        => value.Normalize(NormalizationForm.FormC)
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("/", "%2F", StringComparison.Ordinal);

    private static MeshOptimizerSubMeshSettings Clone(
        MeshOptimizerSubMeshSettings source,
        bool useModelImportDefaults)
        => new()
        {
            UseModelImportDefaults = useModelImportDefaults,
            Meshlets = Clone(source.Meshlets),
            Lods = Clone(source.Lods),
        };

    private static MeshletGenerationSettings Clone(MeshletGenerationSettings source)
        => new()
        {
            Enabled = source.Enabled, BuildMode = source.BuildMode, MaxVertices = source.MaxVertices,
            MinTriangles = source.MinTriangles, MaxTriangles = source.MaxTriangles, ConeWeight = source.ConeWeight,
            SplitFactor = source.SplitFactor, FillWeight = source.FillWeight, OptimizeMeshlets = source.OptimizeMeshlets,
            OptimizeLevel = source.OptimizeLevel, ComputeBounds = source.ComputeBounds, EncodeMeshlets = source.EncodeMeshlets,
            EncodeVertexReferences = source.EncodeVertexReferences,
        };

    private static MeshLodGenerationSettings Clone(MeshLodGenerationSettings source)
        => new()
        {
            Enabled = source.Enabled, Mode = source.Mode, AdditionalLodCount = source.AdditionalLodCount,
            FirstLodIndexRatio = source.FirstLodIndexRatio, LodRatioScale = source.LodRatioScale,
            TargetError = source.TargetError, FirstLodDistance = source.FirstLodDistance,
            LodDistanceScale = source.LodDistanceScale, ReusePreviousLodAsSource = source.ReusePreviousLodAsSource,
            Options = source.Options, UseNormals = source.UseNormals, NormalWeight = source.NormalWeight,
            UseTangents = source.UseTangents, TangentWeight = source.TangentWeight,
            UseTexCoords = source.UseTexCoords, TexCoordWeight = source.TexCoordWeight,
            UseColors = source.UseColors, ColorWeight = source.ColorWeight,
            ProtectAttributeSeams = source.ProtectAttributeSeams,
            PrioritizeBorderVertices = source.PrioritizeBorderVertices,
            LockWeightedVertices = source.LockWeightedVertices,
        };
}

/// <summary>Import-time meshlet cooking diagnostics suitable for import logs.</summary>
public readonly record struct ModelImportMeshletCookResult(
    int SubMeshCount,
    int GeneratedLodCount,
    int PayloadCount,
    int MeshletCount);
