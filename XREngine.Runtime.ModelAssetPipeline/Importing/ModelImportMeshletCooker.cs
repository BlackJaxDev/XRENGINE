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
        // Payloads of an earlier import of this model, validated per mesh before use.
        ModelImportMeshletPayloadCache? cache = OpenPayloadCache(stableModelIdentity);
        int meshletCount = 0;
        int payloadCount = 0;
        int generatedLodCount = 0;
        ClusterHierarchyTotals hierarchyTotals = default;
        // Clothing-layer locks of the skinned meshes, computed once on the first skinned cook miss.
        Dictionary<XRMesh, byte[]>? contactLocks = null;

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
            ApplySkinnedClusterHierarchyPolicy(subMesh);

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
                bool attached = cache?.TryAttach(
                    mesh,
                    entityKey.Value,
                    subMesh.MeshOptimizer.Meshlets,
                    subMesh.MeshOptimizer.Lods) ?? false;
                if (!attached && mesh.HasSkinning && subMesh.MeshOptimizer.Meshlets is { Enabled: true, BuildClusterHierarchy: true })
                {
                    contactLocks ??= SkinnedLayerContactLocks.Compute(CollectSkinnedMeshes(ordered));
                    mesh.SetClusterHierarchyContactLocks(contactLocks.TryGetValue(mesh, out byte[]? locks) ? locks : null);
                }
                long cookCountBefore = MeshOptimizerIntegration.MeshletBuildInvocationCount;
                MeshletPayload payload = mesh.GetOrCreateMeshletPayload(
                    subMesh.MeshOptimizer.Meshlets,
                    subMesh.MeshOptimizer.Lods,
                    entityKey.Value);
                payload.ValidatePortablePayload();
                cache?.Record(payload);
                payloadCount++;
                meshletCount += payload.Meshlets.Length;
                if (payload.HasClusterHierarchy)
                {
                    hierarchyTotals.Add(payload);
                    // The locked-vertex count exists only at cook time; a cache hit leaves it out.
                    if (cookCountBefore != MeshOptimizerIntegration.MeshletBuildInvocationCount && payload.HasClusterSkinning)
                    {
                        hierarchyTotals.SkinnedVertices += MeshOptimizerIntegration.LastClusterHierarchyStats.SkinnedVertexCount;
                        hierarchyTotals.MorphLockedVertices += MeshOptimizerIntegration.LastClusterHierarchyStats.MorphLockedVertexCount;
                        hierarchyTotals.NonFiniteCentroids += MeshOptimizerIntegration.LastClusterHierarchyStats.NonFiniteCentroidCount;
                        hierarchyTotals.ContactLockedVertices += MeshOptimizerIntegration.LastClusterHierarchyStats.ContactLockedVertexCount;
                    }
                }
                mesh.SetClusterHierarchyContactLocks(null);
                lodIndex++;
            }
        }

        if (cache is not null)
            SavePayloadCache(cache);

        cookStopwatch.Stop();
        if (hierarchyTotals.PayloadCount > 0)
            WriteClusterHierarchySummary(stableModelIdentity, hierarchyTotals, cookStopwatch.Elapsed, cache);
        RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordMeshletColdImport(
            buildTime: cookStopwatch.Elapsed,
            allocatedBytes: Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - allocationStart),
            generatedLods: generatedLodCount,
            payloads: payloadCount,
            meshlets: meshletCount);
        return new ModelImportMeshletCookResult(ordered.Count, generatedLodCount, payloadCount, meshletCount);
    }

    /// <summary>Totals of the cluster hierarchy payloads of one model cook, for the cook summary.</summary>
    private struct ClusterHierarchyTotals
    {
        public int PayloadCount;
        public long Clusters;
        public long Level0Clusters;
        public long Groups;
        public int MaxLevels;
        public long SourceTriangles;
        public long HierarchyTriangles;
        public long Level0Triangles;
        public int SkinnedPayloads;
        public long SkinnedClusters;
        public long ClusterBones;
        public int MaxClusterBones;
        public long MorphClusters;
        public long SkinnedVertices;
        public long MorphLockedVertices;
        public long ContactLockedVertices;
        public long NonFiniteCentroids;
        /// <summary>Triangles of the skinned payloads by hierarchy level (index 0 = original geometry).</summary>
        public long[]? SkinnedLevelTriangles;
        /// <summary>Source vertices of the skinned payloads that have a deformation order.</summary>
        public long DeformationOrderVertices;
        /// <summary>
        /// Sum of the deformation prefix counts of the skinned payloads by hierarchy level: the vertices
        /// that an instance clamped at that level deforms. A payload with fewer levels adds its
        /// coarsest prefix to the deeper levels, because a clamp past its last level stops there.
        /// </summary>
        public long[]? DeformationPrefixVertices;
        /// <summary>The most hierarchy levels of one skinned payload with a deformation order.</summary>
        public int DeformationPrefixLevels;

        public void Add(MeshletPayload payload)
        {
            if (payload.HasClusterSkinning)
            {
                SkinnedPayloads++;
                SkinnedClusters += payload.ClusterSkins.Length;
                ClusterBones += payload.ClusterBones.Length;
                foreach (MeshletClusterSkinRecord skin in payload.ClusterSkins)
                {
                    MaxClusterBones = Math.Max(MaxClusterBones, skin.BoneCount);
                    if (skin.HasMorphVertices)
                        MorphClusters++;
                }

                // A cluster's level is 0 for the original geometry, otherwise one more than the
                // depth of the group it was simplified from.
                SkinnedLevelTriangles ??= new long[32];
                for (int i = 0; i < payload.Meshlets.Length; i++)
                {
                    int level = i < payload.ClusterLevel0Count
                        ? 0
                        : payload.LodGroups[payload.ClusterLods[i].RefinedGroup].Depth + 1;
                    SkinnedLevelTriangles[Math.Min(level, SkinnedLevelTriangles.Length - 1)] += payload.Meshlets[i].TriangleCount;
                }

                if (payload.HasDeformationOrder)
                {
                    DeformationOrderVertices += payload.DeformationVertexOrder.Length;
                    DeformationPrefixVertices ??= new long[32];
                    int prefixLevels = payload.DeformationPrefixCounts.Length;
                    DeformationPrefixLevels = Math.Max(DeformationPrefixLevels, Math.Min(prefixLevels, DeformationPrefixVertices.Length));
                    for (int level = 0; level < DeformationPrefixVertices.Length; level++)
                        DeformationPrefixVertices[level] += payload.DeformationPrefixCounts[Math.Min(level, prefixLevels - 1)];
                }
            }
            PayloadCount++;
            Clusters += payload.Meshlets.Length;
            Level0Clusters += payload.ClusterLevel0Count;
            Groups += payload.LodGroups.Length;
            int levels = 0;
            foreach (MeshletLodGroupRecord group in payload.LodGroups)
                levels = Math.Max(levels, group.Depth + 1);
            MaxLevels = Math.Max(MaxLevels, levels);
            SourceTriangles += payload.SourceTriangleCount;
            for (int i = 0; i < payload.Meshlets.Length; i++)
            {
                HierarchyTriangles += payload.Meshlets[i].TriangleCount;
                if (i < payload.ClusterLevel0Count)
                    Level0Triangles += payload.Meshlets[i].TriangleCount;
            }
        }
    }

    /// <summary>
    /// Writes one line per model cook with cluster hierarchy payloads to meshlet-cache.log: payloads,
    /// clusters (all levels and level 0), groups, the deepest hierarchy, triangle totals, the cook
    /// time (cache hits included) and the size of the model's meshlet cache file.
    /// </summary>
    private static void WriteClusterHierarchySummary(string modelIdentity, ClusterHierarchyTotals totals, TimeSpan cookTime, ModelImportMeshletPayloadCache? cache)
    {
        long cacheBytes = cache is not null && File.Exists(cache.FilePath) ? new FileInfo(cache.FilePath).Length : -1L;
        Debug.WriteAuxiliaryLog(
            "meshlet-cache.log",
            $"[ClusterHierarchy] model={modelIdentity} payloads={totals.PayloadCount} clusters={totals.Clusters} level0Clusters={totals.Level0Clusters} " +
            $"groups={totals.Groups} maxLevels={totals.MaxLevels} sourceTriangles={totals.SourceTriangles} level0Triangles={totals.Level0Triangles} " +
            $"allLevelTriangles={totals.HierarchyTriangles} triangleRatio={(totals.Level0Triangles == 0 ? 0.0 : (double)totals.HierarchyTriangles / totals.Level0Triangles):F2} " +
            $"cookMs={cookTime.TotalMilliseconds:F0} cacheBytes={cacheBytes} cacheHits={cache?.HitCount ?? 0} cacheMisses={cache?.MissCount ?? 0}");
        if (totals.SkinnedPayloads > 0)
        {
            long[] levels = totals.SkinnedLevelTriangles ?? [];
            int lastLevel = levels.Length - 1;
            while (lastLevel > 0 && levels[lastLevel] == 0)
                lastLevel--;
            string levelTriangles = string.Join(',', levels.Take(lastLevel + 1));
            Debug.WriteAuxiliaryLog(
                "meshlet-cache.log",
                $"[ClusterHierarchySkinned] model={modelIdentity} payloads={totals.SkinnedPayloads} clusters={totals.SkinnedClusters} " +
                $"bonesPerCluster={(totals.SkinnedClusters == 0 ? 0.0 : (double)totals.ClusterBones / totals.SkinnedClusters):F2} " +
                $"maxClusterBones={totals.MaxClusterBones} morphClusters={totals.MorphClusters} " +
                $"skinnedVertices={totals.SkinnedVertices} morphLockedVertices={totals.MorphLockedVertices} " +
                $"lockedShare={(totals.SkinnedVertices == 0 ? 0.0 : (double)totals.MorphLockedVertices / totals.SkinnedVertices):P1} " +
                $"contactLockedVertices={totals.ContactLockedVertices} " +
                $"nonFiniteCentroids={totals.NonFiniteCentroids} levelTriangles={levelTriangles} (vertex counts only for payloads cooked in this process)");

            // The share of the skinned source vertices that an instance clamped at each level deforms.
            // Blendshape locks keep moved vertices in every level, so this share shows how much a far
            // instance can save.
            if (totals.DeformationOrderVertices > 0 && totals.DeformationPrefixVertices is { } prefix)
            {
                string shares = string.Join(',', prefix.Take(totals.DeformationPrefixLevels)
                    .Select(count => ((double)count / totals.DeformationOrderVertices).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));
                Debug.WriteAuxiliaryLog(
                    "meshlet-cache.log",
                    $"[DeformationPrefix] model={modelIdentity} vertices={totals.DeformationOrderVertices} levelShares={shares}");
            }
        }

        // Crack-free checks of the hierarchies built (not read from the cache) in this process.
        MeshletClusterHierarchyBuilder.InvariantReport checks = MeshletClusterHierarchyBuilder.InvariantTotals;
        Debug.WriteAuxiliaryLog(
            "meshlet-cache.log",
            $"[ClusterHierarchyChecks] cookVersion={MeshletClusterHierarchyBuilder.CookVersion} " +
            $"self={checks.SelfFailures}/{checks.SelfChecks} monotonic={checks.MonotonicFailures}/{checks.MonotonicChecks} " +
            $"containment={checks.ContainmentFailures}/{checks.ContainmentChecks} (failures/checks) " +
            $"borderRejected={checks.BorderRejections}/{checks.BorderChecks} (rejected simplifications/checks) " +
            $"ended empty={checks.EndedEmpty} noReduction={checks.EndedNoReduction} nonFiniteError={checks.EndedNonFiniteError}");
    }

    private static ModelImportMeshletPayloadCache? OpenPayloadCache(string stableModelIdentity)
    {
        ModelImportMeshletPayloadCache? cache = ModelImportMeshletPayloadCache.TryOpen(
            RuntimeRenderingHostServices.GameCachePath,
            stableModelIdentity);
        if (cache?.LoadFailure is { } failure)
            Debug.WriteAuxiliaryLog("meshlet-cache.log", $"[MeshletCache] unreadable file {cache.FilePath}; cooking. {failure}");
        return cache;
    }

    private static void SavePayloadCache(ModelImportMeshletPayloadCache cache)
    {
        bool written = false;
        string? failure = null;
        try
        {
            written = cache.SaveIfChanged();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // A failed write costs only a rebuild on the next import.
            failure = $"{exception.GetType().Name}: {exception.Message}";
        }

        Debug.WriteAuxiliaryLog(
            "meshlet-cache.log",
            $"[MeshletCache] file={Path.GetFileName(cache.FilePath)} stored={cache.StoredCount} hits={cache.HitCount} " +
            $"misses={cache.MissCount} rejected={cache.RejectedCount} written={written}{(failure is null ? string.Empty : $" writeFailure={failure}")}");
    }

    /// <summary>
    /// Returns the distinct meshes of every LOD of the skinned sub-meshes, the surfaces that
    /// <see cref="SkinnedLayerContactLocks"/> compares for clothing layers.
    /// </summary>
    private static List<XRMesh> CollectSkinnedMeshes(List<SubMesh> subMeshes)
    {
        var meshes = new List<XRMesh>();
        var seen = new HashSet<XRMesh>(ReferenceEqualityComparer.Instance);
        foreach (SubMesh subMesh in subMeshes)
            foreach (SubMeshLOD lod in subMesh.LODs)
                if (lod.Mesh is { HasSkinning: true } mesh && seen.Add(mesh))
                    meshes.Add(mesh);
        return meshes;
    }

    /// <summary>
    /// Gives a skinned sub-mesh that builds meshlets a cluster hierarchy, so skinned meshes use
    /// virtual geometry like static meshes. The sub-mesh gets its own settings copy with the flag
    /// set, so its payload hash and freshness checks see the settings that cooked it. A sub-mesh
    /// is skinned when any of its LOD meshes has bone influences.
    /// </summary>
    private static void ApplySkinnedClusterHierarchyPolicy(SubMesh subMesh)
    {
        MeshletGenerationSettings meshlets = subMesh.MeshOptimizer.Meshlets;
        if (!meshlets.Enabled || meshlets.BuildClusterHierarchy)
            return;

        bool skinned = false;
        foreach (SubMeshLOD lod in subMesh.LODs)
            skinned |= lod.Mesh?.HasSkinning == true;
        if (!skinned)
            return;

        MeshletGenerationSettings hierarchy = Clone(meshlets);
        hierarchy.BuildClusterHierarchy = true;
        subMesh.MeshOptimizer.Meshlets = hierarchy;
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
            EncodeVertexReferences = source.EncodeVertexReferences, BuildClusterHierarchy = source.BuildClusterHierarchy,
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
