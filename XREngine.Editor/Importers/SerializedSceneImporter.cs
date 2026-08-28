using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    private static readonly Regex DocumentHeaderRegex = new(
        @"^---\s*!u!(?<classId>-?\d+)\s*&(?<fileId>-?\d+)(?<stripped>\s+stripped)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SceneNode[] Import(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string normalizedPath = Path.GetFullPath(filePath);
        var context = new SourceProjectImportContext(normalizedPath);
        context.DiscoverDependencies();
        var state = new ImportState(context);
        ImportedHierarchy hierarchy = ImportHierarchy(normalizedPath, state);
        context.MarkOutcome(normalizedPath, SourceImportConversionOutcome.Converted);
        return [.. hierarchy.RootEntries.Select(static entry => entry.Node)];
    }

    public static SceneNode ImportPrefab(string filePath)
        => ImportPrefabWithManifest(filePath).RootNode
            ?? throw new InvalidDataException($"Unity prefab import produced no hierarchy for '{filePath}'.");

    public static SerializedPrefabConversionResult ImportPrefabWithManifest(string filePath)
        => ImportPrefabWithManifest(filePath, outputDestination: null, explicitProjectOrAssetsRoot: null);

    public static SerializedPrefabConversionResult ImportPrefabWithManifest(
        string filePath,
        string? outputDestination,
        string? explicitProjectOrAssetsRoot)
        => ImportPrefabWithManifest(
            filePath,
            outputDestination,
            explicitProjectOrAssetsRoot,
            cancellationToken: default,
            progress: null);

    /// <summary>
    /// Converts, composes, and cooks a Unity prefab as one publication unit.
    /// Meshlet identities are assigned after all imported and YAML-authored
    /// hierarchy changes have completed.
    /// </summary>
    public static SerializedPrefabConversionResult ImportPrefabWithManifest(
        string filePath,
        string? outputDestination,
        string? explicitProjectOrAssetsRoot,
        ModelCookSettings cookSettings,
        ModelCookOverrideSnapshot cookOverrides)
        => ImportPrefabWithManifest(
            filePath,
            outputDestination,
            explicitProjectOrAssetsRoot,
            cancellationToken: default,
            progress: null,
            cookSettings,
            cookOverrides);

    public static SerializedPrefabConversionResult ImportPrefabWithManifest(
        string filePath,
        string? outputDestination,
        string? explicitProjectOrAssetsRoot,
        CancellationToken cancellationToken,
        Action<float, string>? progress)
        => ImportPrefabWithManifest(
            filePath,
            outputDestination,
            explicitProjectOrAssetsRoot,
            cancellationToken,
            progress,
            new ModelCookSettings(),
            ModelCookOverrideSnapshot.Empty);

    private static SerializedPrefabConversionResult ImportPrefabWithManifest(
        string filePath,
        string? outputDestination,
        string? explicitProjectOrAssetsRoot,
        CancellationToken cancellationToken,
        Action<float, string>? progress,
        ModelCookSettings cookSettings,
        ModelCookOverrideSnapshot cookOverrides)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(cookSettings);
        ArgumentNullException.ThrowIfNull(cookOverrides);

        string normalizedPath = Path.GetFullPath(filePath);
        var context = new SourceProjectImportContext(
            normalizedPath,
            outputDestination,
            explicitProjectOrAssetsRoot,
            cancellationToken,
            progress);
        context.DiscoverDependencies();
        var state = new ImportState(context);
        ImportedHierarchy hierarchy = ImportHierarchy(normalizedPath, state);
        hierarchy.SortRoots();

        SceneNode rootNode;
        if (hierarchy.RootEntries.Count == 1)
        {
            rootNode = hierarchy.RootEntries[0].Node;
        }
        else
        {
            string rootName = Path.GetFileNameWithoutExtension(normalizedPath);
            rootNode = new SceneNode(rootName, new Transform());
            rootNode.AdoptPersistentID(PersistentObjectID.FromIdentity(
                $"xrengine:unity:synthetic-root:{normalizedPath.ToLowerInvariant()}"));
            rootNode.Transform.AdoptPersistentID(PersistentObjectID.FromIdentity(
                $"xrengine:unity:synthetic-root-transform:{normalizedPath.ToLowerInvariant()}"));
            foreach (ImportedRootEntry rootEntry in hierarchy.RootEntries)
                rootEntry.Node.Parent = rootNode;
        }

        context.MarkOutcome(normalizedPath, SourceImportConversionOutcome.Converted);
        AssignFinalMeshletEntityIdentities(rootNode);
        string meshletCookIdentity = ModelCacheSourceIdentityResolver.Resolve(
            normalizedPath,
            RuntimeModelImportServices.Current.ProjectAssetsRoot,
            RuntimeModelImportServices.Current.EngineAssetsRoot).IdentityHash;
        ModelImportMeshletCooker.CookScene(rootNode, cookSettings, meshletCookIdentity, cookOverrides);
        bool behaviorErrors = context.Diagnostics.Any(static diagnostic =>
            diagnostic.Category == SourceImportDiagnosticCategory.AvatarComponent &&
            diagnostic.Severity == SourceImportDiagnosticSeverity.Error);
        SerializedPrefabImportManifest manifest = context.CreateManifest(
            behaviorErrors
                ? SourceImportCompletionTier.VisualPrefab
                : SourceImportCompletionTier.VisualAndAvatarBehavior);
        SerializedPrefabDependencyMonitor.Register(manifest);
        return new SerializedPrefabConversionResult
        {
            RootNode = rootNode,
            Manifest = manifest,
            MeshletCookingCompleted = true,
        };
    }

    /// <summary>
    /// Replaces importer-local mesh identities with identities owned by the
    /// completed Unity prefab. Renderer persistent IDs come from Unity GUID and
    /// fileID mappings, so the cook key survives process restarts and does not
    /// accidentally collide when the same external model is instantiated twice.
    /// </summary>
    private static void AssignFinalMeshletEntityIdentities(SceneNode node)
    {
        ModelComponent[] components = node.GetComponents<ModelComponent>().ToArray();
        for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
        {
            ModelComponent component = components[componentIndex];
            if (component.Model is not { } model)
                continue;

            string componentIdentity = component.ID.ToString("N", CultureInfo.InvariantCulture);
            for (int subMeshIndex = 0; subMeshIndex < model.Meshes.Count; subMeshIndex++)
            {
                SubMesh subMesh = model.Meshes[subMeshIndex];
                subMesh.ImportedEntityIdentity = $"unity-renderer:{componentIdentity}/submesh:{subMeshIndex}";
                subMesh.ImportedEntityIdentityIsStable = true;
            }
        }

        foreach (TransformBase childTransform in node.Transform.Children)
            if (childTransform.SceneNode is SceneNode childNode)
                AssignFinalMeshletEntityIdentities(childNode);
    }

    private static ImportedHierarchy ImportHierarchy(string filePath, ImportState state)
    {
        if (!state.ActiveImports.Add(filePath))
        {
            Debug.LogWarning($"Skipping recursive Unity import for '{filePath}' because the file is already being processed.");
            return new ImportedHierarchy(filePath);
        }

        try
        {
            ParsedSourceFile parsed = ParseSourceFile(filePath);
            var hierarchy = new ImportedHierarchy(filePath);
            if (state.Context.GuidIndex.TryGetGuid(filePath, out string? sourceGuid))
                hierarchy.SourceGuid = sourceGuid;

            foreach (ParsedTransform parsedTransform in parsed.Transforms.Values
                .Where(static transform => !transform.IsStripped)
                .OrderBy(static transform => transform.DocumentOrder))
            {
                if (!parsed.GameObjects.TryGetValue(parsedTransform.GameObjectFileId, out ParsedGameObject? gameObject) ||
                    gameObject.IsStripped)
                {
                    state.Context.AddDiagnostic(
                        "UNITYPREFAB0001",
                        SourceImportDiagnosticSeverity.Warning,
                        SourceImportDiagnosticCategory.PrefabOverride,
                        $"Transform '{parsedTransform.FileId}' has no non-stripped GameObject and was skipped instead of creating a phantom node.",
                        filePath);
                    continue;
                }

                var transform = new Transform
                {
                    Translation = ConvertPosition(parsedTransform.LocalPosition),
                    Rotation = ConvertRotation(parsedTransform.LocalRotation),
                    Scale = parsedTransform.LocalScale,
                };

                var node = new SceneNode(gameObject.Name, transform)
                {
                    IsActiveSelf = gameObject.IsActive,
                    Layer = gameObject.Layer,
                };

                hierarchy.NodesByTransformId[parsedTransform.FileId] = node;
                hierarchy.NodesByGameObjectId[gameObject.FileId] = node;
                hierarchy.TransformSortOrders[parsedTransform.FileId] = parsedTransform.RootOrder ?? parsedTransform.DocumentOrder;
            }

            foreach (ParsedTransform parsedTransform in parsed.Transforms.Values
                .Where(static transform => !transform.IsStripped)
                .OrderBy(static transform => transform.DocumentOrder))
            {
                if (!hierarchy.NodesByTransformId.TryGetValue(parsedTransform.FileId, out SceneNode? parentNode))
                    continue;

                foreach (long childTransformFileId in parsedTransform.ChildTransformFileIds)
                {
                    if (hierarchy.NodesByTransformId.TryGetValue(childTransformFileId, out SceneNode? childNode))
                        childNode.Parent = parentNode;
                }
            }

            foreach (ParsedTransform parsedTransform in parsed.Transforms.Values
                .Where(static transform => !transform.IsStripped)
                .OrderBy(static transform => transform.DocumentOrder))
            {
                if (parsedTransform.ParentTransformFileId == 0 ||
                    !hierarchy.NodesByTransformId.TryGetValue(parsedTransform.FileId, out SceneNode? childNode) ||
                    childNode.Parent is not null ||
                    !hierarchy.NodesByTransformId.TryGetValue(parsedTransform.ParentTransformFileId, out SceneNode? parentNode))
                {
                    continue;
                }

                childNode.Parent = parentNode;
            }

            AttachSupportedComponents(parsed, hierarchy, state);
            RegisterIdentityMappings(hierarchy);

            var prefabRootsByInstanceId = new Dictionary<long, List<ImportedRootEntry>>();
            foreach (ParsedPrefabInstance prefabInstance in parsed.PrefabInstances.OrderBy(static instance => instance.DocumentOrder))
            {
                ImportedHierarchy importedPrefab = ImportPrefabInstance(prefabInstance, parsed, hierarchy, state);
                importedPrefab.SortRoots();
                BindStrippedProxies(prefabInstance, parsed, hierarchy, importedPrefab, state);

                if (prefabInstance.TransformParentFileId != 0 &&
                    hierarchy.NodesByTransformId.TryGetValue(prefabInstance.TransformParentFileId, out SceneNode? parentNode))
                {
                    foreach (ImportedRootEntry rootEntry in importedPrefab.RootEntries)
                        rootEntry.Node.Parent = parentNode;
                }

                prefabRootsByInstanceId[prefabInstance.FileId] = [.. importedPrefab.RootEntries];
            }

            AttachAvatarComponents(parsed, hierarchy, state);
            RegisterIdentityMappings(hierarchy);
            ReorderChildren(parsed, hierarchy, prefabRootsByInstanceId);
            PopulateRootEntries(parsed, hierarchy, prefabRootsByInstanceId);
            hierarchy.SortRoots();
            return hierarchy;
        }
        finally
        {
            state.ActiveImports.Remove(filePath);
        }
    }

    private static ImportedHierarchy ImportPrefabInstance(
        ParsedPrefabInstance prefabInstance,
        ParsedSourceFile ownerFile,
        ImportedHierarchy ownerHierarchy,
        ImportState state)
    {
        string? prefabPath = ResolveAssetPath(state, prefabInstance.SourcePrefab.Guid);
        ImportedHierarchy hierarchy;

        if (!string.IsNullOrWhiteSpace(prefabPath) && File.Exists(prefabPath))
        {
            string extension = Path.GetExtension(prefabPath);
            if (string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase))
            {
                hierarchy = ImportHierarchy(prefabPath, state);
            }
            else if (SupportedExternalModelExtensions.Contains(extension))
            {
                hierarchy = ImportModelHierarchy(prefabPath, prefabInstance.SourcePrefab.Guid, state);
            }
            else
            {
                throw new SourceVisualImportException(
                    $"Unity PrefabInstance source '{prefabPath}' has unsupported asset type '{extension}'.");
            }
        }
        else
        {
            string placeholderName = ExtractStringOverride(prefabInstance.Modifications, "m_Name")
                ?? (!string.IsNullOrWhiteSpace(prefabPath)
                    ? Path.GetFileNameWithoutExtension(prefabPath)
                    : $"Missing Prefab {prefabInstance.SourcePrefab.Guid ?? prefabInstance.FileId.ToString(CultureInfo.InvariantCulture)}");

            throw new SourceVisualImportException(
                $"Required Unity prefab source '{prefabInstance.SourcePrefab.Guid ?? "<missing-guid>"}' could not be resolved while importing '{state.EntryFilePath}' (suggested name '{placeholderName}').");
        }

        ApplyPrefabModifications(prefabInstance, hierarchy, ownerHierarchy.SourcePath, state);
        ApplyPrefabRemovals(prefabInstance, hierarchy);
        ApplyPrefabAdditions(prefabInstance, ownerFile, ownerHierarchy, hierarchy, state);
        hierarchy.SortRoots();
        return hierarchy;
    }

    private static ImportedHierarchy CreatePlaceholderHierarchy(string rootName, int documentOrder)
    {
        var hierarchy = new ImportedHierarchy(rootName);
        var node = new SceneNode(rootName, new Transform());
        hierarchy.RootEntries.Add(new ImportedRootEntry(node, transformFileId: null, sortOrder: documentOrder, discoveryOrder: documentOrder));
        return hierarchy;
    }

    private static void BindStrippedProxies(
        ParsedPrefabInstance prefabInstance,
        ParsedSourceFile ownerFile,
        ImportedHierarchy ownerHierarchy,
        ImportedHierarchy importedHierarchy,
        ImportState state)
    {
        foreach (ParsedStrippedProxy proxy in ownerFile.StrippedProxies.Values
            .Where(proxy => proxy.PrefabInstanceFileId == prefabInstance.FileId)
            .OrderBy(static proxy => proxy.DocumentOrder))
        {
            long sourceFileId = proxy.CorrespondingSourceObject.FileId;
            switch (proxy.ObjectKind)
            {
                case SourceAssetObjectKind.GameObject:
                    if (importedHierarchy.NodesByGameObjectId.TryGetValue(sourceFileId, out SceneNode? gameObjectNode))
                    {
                        ownerHierarchy.NodesByGameObjectId[proxy.LocalFileId] = gameObjectNode;
                        break;
                    }

                    ReportUnresolvedProxy(proxy, state);
                    break;

                case SourceAssetObjectKind.Transform:
                    if (importedHierarchy.NodesByTransformId.TryGetValue(sourceFileId, out SceneNode? transformNode))
                    {
                        ownerHierarchy.NodesByTransformId[proxy.LocalFileId] = transformNode;
                        break;
                    }

                    ReportUnresolvedProxy(proxy, state);
                    break;

                case SourceAssetObjectKind.Renderer:
                case SourceAssetObjectKind.Component:
                    if (importedHierarchy.ComponentsByFileId.TryGetValue(sourceFileId, out XRComponent? component))
                    {
                        ownerHierarchy.ComponentsByFileId[proxy.LocalFileId] = component;
                        break;
                    }

                    ReportUnresolvedProxy(proxy, state);
                    break;
            }
        }
    }

    private static void ReportUnresolvedProxy(ParsedStrippedProxy proxy, ImportState state)
    {
        state.Context.AddDiagnostic(
            "UNITYMODEL0001",
            SourceImportDiagnosticSeverity.Error,
            SourceImportDiagnosticCategory.ModelIdentity,
            $"Stripped {proxy.ObjectKind} proxy '{proxy.LocalFileId}' could not resolve source fileID " +
            $"'{proxy.CorrespondingSourceObject.FileId}' from GUID '{proxy.CorrespondingSourceObject.Guid}'.",
            state.EntryFilePath,
            identity: new SourceAssetIdentity
            {
                AssetGuid = proxy.CorrespondingSourceObject.Guid ?? string.Empty,
                LocalFileId = proxy.CorrespondingSourceObject.FileId,
                ObjectKind = proxy.ObjectKind,
            });
    }

    private static void RegisterIdentityMappings(ImportedHierarchy hierarchy)
    {
        if (string.IsNullOrWhiteSpace(hierarchy.SourceGuid))
            return;

        foreach ((long fileId, SceneNode node) in hierarchy.NodesByGameObjectId)
        {
            var identity = new SourceAssetIdentity
            {
                AssetGuid = hierarchy.SourceGuid,
                LocalFileId = fileId,
                ObjectKind = SourceAssetObjectKind.GameObject,
            };
            node.AdoptPersistentID(identity.ToPersistentID());
            hierarchy.NodesByIdentity[identity] = node;
        }

        foreach ((long fileId, SceneNode node) in hierarchy.NodesByTransformId)
        {
            var identity = new SourceAssetIdentity
            {
                AssetGuid = hierarchy.SourceGuid,
                LocalFileId = fileId,
                ObjectKind = SourceAssetObjectKind.Transform,
            };
            node.Transform.AdoptPersistentID(identity.ToPersistentID());
            hierarchy.NodesByIdentity[identity] = node;
        }

        foreach ((long fileId, XRComponent component) in hierarchy.ComponentsByFileId)
        {
            var identity = new SourceAssetIdentity
            {
                AssetGuid = hierarchy.SourceGuid,
                LocalFileId = fileId,
                ObjectKind = component is XREngine.Components.Scene.Mesh.ModelComponent
                    ? SourceAssetObjectKind.Renderer
                    : SourceAssetObjectKind.Component,
            };
            component.AdoptPersistentID(identity.ToPersistentID());
            hierarchy.ComponentsByIdentity[identity] = component;
        }
    }

    private static void ApplyPrefabModifications(
        ParsedPrefabInstance prefabInstance,
        ImportedHierarchy hierarchy,
        string modificationSourcePath,
        ImportState state)
    {
        foreach (IGrouping<long, PropertyModification> group in prefabInstance.Modifications
            .Where(static modification => !string.IsNullOrWhiteSpace(modification.PropertyPath))
            .GroupBy(static modification => modification.TargetFileId))
        {
            bool targetResolved = false;
            if (hierarchy.NodesByGameObjectId.TryGetValue(group.Key, out SceneNode? node))
            {
                ApplyGameObjectModifications(node, group);
                targetResolved = true;
            }

            if (hierarchy.NodesByTransformId.TryGetValue(group.Key, out SceneNode? transformNode) &&
                transformNode.Transform is Transform transform)
            {
                ApplyTransformModifications(group.Key, transform, group, hierarchy);
                targetResolved = true;
            }

            if (hierarchy.ComponentsByFileId.TryGetValue(group.Key, out XRComponent? component))
            {
                ApplyComponentModifications(component, group, state);
                targetResolved = true;
            }

            if (targetResolved)
                continue;

            bool hadVisualOverrides = group.Any(static modification =>
                IsRequiredVisualOverride(modification.PropertyPath));
            state.Context.IgnoreStalePrefabModification(modificationSourcePath, group.Key);
            state.Context.AddDiagnostic(
                "UNITYOVERRIDE0003",
                hadVisualOverrides
                    ? SourceImportDiagnosticSeverity.Warning
                    : SourceImportDiagnosticSeverity.Info,
                SourceImportDiagnosticCategory.PrefabOverride,
                $"Ignored stale prefab modification target fileID '{group.Key}' because the current source asset " +
                $"does not generate that object. Properties: " +
                $"{string.Join(", ", group.Select(static modification => modification.PropertyPath).Distinct(StringComparer.Ordinal))}",
                hierarchy.SourcePath,
                identity: new SourceAssetIdentity
                {
                    AssetGuid = hierarchy.SourceGuid ?? string.Empty,
                    LocalFileId = group.Key,
                    ObjectKind = SourceAssetObjectKind.Component,
                });
        }
    }

    private static bool IsRequiredVisualOverride(string propertyPath)
        => propertyPath.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) ||
           propertyPath.StartsWith("m_BlendShapeWeights.Array.data[", StringComparison.Ordinal) ||
           propertyPath.StartsWith("m_AABB.", StringComparison.Ordinal) ||
           propertyPath.StartsWith("m_LocalAABB.", StringComparison.Ordinal) ||
           propertyPath is "m_Enabled" or "m_CastShadows" or "m_ReceiveShadows";

    private static void ApplyGameObjectModifications(SceneNode node, IEnumerable<PropertyModification> modifications)
    {
        foreach (PropertyModification modification in modifications)
        {
            switch (modification.PropertyPath)
            {
                case "m_Name":
                    if (!string.IsNullOrWhiteSpace(modification.Value))
                        node.Name = modification.Value;
                    break;
                case "m_IsActive":
                    if (TryParseBool(modification.Value, out bool isActive))
                        node.IsActiveSelf = isActive;
                    break;
                case "m_Layer":
                    if (TryParseInt(modification.Value, out int layer))
                        node.Layer = layer;
                    break;
            }
        }
    }

    private static void ApplyTransformModifications(long transformFileId, Transform transform, IEnumerable<PropertyModification> modifications, ImportedHierarchy hierarchy)
    {
        Vector3 sourcePosition = ConvertPosition(transform.Translation);
        Quaternion sourceRotation = ConvertRotation(transform.Rotation);
        Vector3 sourceScale = transform.Scale;
        int? explicitSortOrder = null;

        foreach (PropertyModification modification in modifications)
        {
            switch (modification.PropertyPath)
            {
                case "m_LocalPosition.x":
                    if (TryParseFloat(modification.Value, out float positionX))
                        sourcePosition.X = positionX;
                    break;
                case "m_LocalPosition.y":
                    if (TryParseFloat(modification.Value, out float positionY))
                        sourcePosition.Y = positionY;
                    break;
                case "m_LocalPosition.z":
                    if (TryParseFloat(modification.Value, out float positionZ))
                        sourcePosition.Z = positionZ;
                    break;
                case "m_LocalRotation.x":
                    if (TryParseFloat(modification.Value, out float rotationX))
                        sourceRotation.X = rotationX;
                    break;
                case "m_LocalRotation.y":
                    if (TryParseFloat(modification.Value, out float rotationY))
                        sourceRotation.Y = rotationY;
                    break;
                case "m_LocalRotation.z":
                    if (TryParseFloat(modification.Value, out float rotationZ))
                        sourceRotation.Z = rotationZ;
                    break;
                case "m_LocalRotation.w":
                    if (TryParseFloat(modification.Value, out float rotationW))
                        sourceRotation.W = rotationW;
                    break;
                case "m_LocalScale.x":
                    if (TryParseFloat(modification.Value, out float scaleX))
                        sourceScale.X = scaleX;
                    break;
                case "m_LocalScale.y":
                    if (TryParseFloat(modification.Value, out float scaleY))
                        sourceScale.Y = scaleY;
                    break;
                case "m_LocalScale.z":
                    if (TryParseFloat(modification.Value, out float scaleZ))
                        sourceScale.Z = scaleZ;
                    break;
                case "m_RootOrder":
                    if (TryParseInt(modification.Value, out int rootOrder))
                        explicitSortOrder = rootOrder;
                    break;
            }
        }

        transform.Translation = ConvertPosition(sourcePosition);
        transform.Rotation = ConvertRotation(sourceRotation);
        transform.Scale = sourceScale;

        if (explicitSortOrder.HasValue)
            hierarchy.SetTransformSortOrder(transformFileId, explicitSortOrder.Value);
    }

    private static void ReorderChildren(
        ParsedSourceFile parsed,
        ImportedHierarchy hierarchy,
        IReadOnlyDictionary<long, List<ImportedRootEntry>> prefabRootsByInstanceId)
    {
        var prefabChildrenByParent = new Dictionary<long, List<(ImportedRootEntry root, int instanceOrder, int rootOffset)>>();
        foreach (ParsedPrefabInstance prefabInstance in parsed.PrefabInstances)
        {
            if (prefabInstance.TransformParentFileId == 0 ||
                !prefabRootsByInstanceId.TryGetValue(prefabInstance.FileId, out List<ImportedRootEntry>? importedRoots))
            {
                continue;
            }

            if (!prefabChildrenByParent.TryGetValue(prefabInstance.TransformParentFileId, out List<(ImportedRootEntry root, int instanceOrder, int rootOffset)>? entries))
            {
                entries = [];
                prefabChildrenByParent[prefabInstance.TransformParentFileId] = entries;
            }

            for (int index = 0; index < importedRoots.Count; index++)
                entries.Add((importedRoots[index], prefabInstance.DocumentOrder, index));
        }

        foreach (ParsedTransform parsedTransform in parsed.Transforms.Values
            .Where(static transform => !transform.IsStripped)
            .OrderBy(static transform => transform.DocumentOrder))
        {
            if (!hierarchy.NodesByTransformId.TryGetValue(parsedTransform.FileId, out SceneNode? parentNode))
                continue;

            var orderedChildren = new List<(SceneNode node, int sortOrder, int discoveryOrder)>();
            for (int index = 0; index < parsedTransform.ChildTransformFileIds.Count; index++)
            {
                long childTransformFileId = parsedTransform.ChildTransformFileIds[index];
                if (hierarchy.NodesByTransformId.TryGetValue(childTransformFileId, out SceneNode? childNode))
                    orderedChildren.Add((childNode, index, orderedChildren.Count));
            }

            if (prefabChildrenByParent.TryGetValue(parsedTransform.FileId, out List<(ImportedRootEntry root, int instanceOrder, int rootOffset)>? prefabChildren))
            {
                foreach ((ImportedRootEntry root, int instanceOrder, int rootOffset) in prefabChildren)
                {
                    orderedChildren.Add((
                        root.Node,
                        root.SortOrder,
                        parsedTransform.ChildTransformFileIds.Count + instanceOrder + rootOffset));
                }
            }

            if (orderedChildren.Count == 0)
                continue;

            List<TransformBase> orderedTransforms = [];
            foreach (TransformBase childTransform in orderedChildren
                .OrderBy(static entry => entry.sortOrder)
                .ThenBy(static entry => entry.discoveryOrder)
                .Select(static entry => entry.node.Transform))
            {
                if (!orderedTransforms.Any(existing => ReferenceEquals(existing, childTransform)))
                    orderedTransforms.Add(childTransform);
            }

            SetOrderedChildren(parentNode, orderedTransforms);
        }
    }

    private static void PopulateRootEntries(
        ParsedSourceFile parsed,
        ImportedHierarchy hierarchy,
        IReadOnlyDictionary<long, List<ImportedRootEntry>> prefabRootsByInstanceId)
    {
        hierarchy.RootEntries.Clear();

        if (parsed.SceneRootReferences.Count > 0)
        {
            int discoveryOrder = 0;
            foreach (long rootReference in parsed.SceneRootReferences)
            {
                if (hierarchy.ExcludedRootTransformIds.Contains(rootReference))
                    continue;

                if (hierarchy.NodesByTransformId.TryGetValue(rootReference, out SceneNode? directRoot))
                {
                    hierarchy.RootEntries.Add(new ImportedRootEntry(directRoot, rootReference, discoveryOrder, discoveryOrder));
                    discoveryOrder++;
                    continue;
                }

                if (!prefabRootsByInstanceId.TryGetValue(rootReference, out List<ImportedRootEntry>? prefabRoots))
                    continue;

                foreach (ImportedRootEntry prefabRoot in prefabRoots)
                {
                    hierarchy.RootEntries.Add(new ImportedRootEntry(prefabRoot.Node, prefabRoot.TransformFileId, discoveryOrder, discoveryOrder));
                    discoveryOrder++;
                }
            }

            return;
        }

        int fallbackOrder = 0;
        foreach (ParsedTransform parsedTransform in parsed.Transforms.Values
            .Where(static transform => !transform.IsStripped)
            .OrderBy(static transform => transform.DocumentOrder))
        {
            if (parsedTransform.ParentTransformFileId != 0 ||
                hierarchy.ExcludedRootTransformIds.Contains(parsedTransform.FileId) ||
                !hierarchy.NodesByTransformId.TryGetValue(parsedTransform.FileId, out SceneNode? directRoot))
            {
                continue;
            }

            int sortOrder = parsedTransform.RootOrder ?? fallbackOrder;
            hierarchy.RootEntries.Add(new ImportedRootEntry(directRoot, parsedTransform.FileId, sortOrder, fallbackOrder));
            fallbackOrder++;
        }

        foreach (ParsedPrefabInstance prefabInstance in parsed.PrefabInstances.OrderBy(static instance => instance.DocumentOrder))
        {
            if (prefabInstance.TransformParentFileId != 0 ||
                !prefabRootsByInstanceId.TryGetValue(prefabInstance.FileId, out List<ImportedRootEntry>? prefabRoots))
            {
                continue;
            }

            foreach (ImportedRootEntry prefabRoot in prefabRoots)
            {
                hierarchy.RootEntries.Add(new ImportedRootEntry(prefabRoot.Node, prefabRoot.TransformFileId, prefabRoot.SortOrder, fallbackOrder));
                fallbackOrder++;
            }
        }
    }

    private static ParsedSourceFile ParseSourceFile(string filePath)
    {
        var gameObjects = new Dictionary<long, ParsedGameObject>();
        var transforms = new Dictionary<long, ParsedTransform>();
        var camerasByGameObjectId = new Dictionary<long, ParsedCamera>();
        var lightsByGameObjectId = new Dictionary<long, ParsedLight>();
        var meshFiltersByGameObjectId = new Dictionary<long, ParsedMeshFilter>();
        var meshRenderersByGameObjectId = new Dictionary<long, ParsedMeshRenderer>();
        var skinnedMeshRenderersByGameObjectId = new Dictionary<long, ParsedSkinnedMeshRenderer>();
        var componentsByFileId = new Dictionary<long, ParsedSourceComponent>();
        var monoBehaviours = new List<ParsedMonoBehaviour>();
        var prefabInstances = new List<ParsedPrefabInstance>();
        var sceneRootReferences = new List<long>();
        var strippedProxies = new Dictionary<long, ParsedStrippedProxy>();

        int? classId = null;
        long fileId = 0;
        bool isStripped = false;
        int documentOrder = 0;
        var bodyBuilder = new StringBuilder();

        void FlushDocument()
        {
            if (classId is null || bodyBuilder.Length == 0)
                return;

            ProcessDocument(
                classId.Value,
                fileId,
                isStripped,
                bodyBuilder.ToString(),
                documentOrder++,
                gameObjects,
                transforms,
                camerasByGameObjectId,
                lightsByGameObjectId,
                meshFiltersByGameObjectId,
                meshRenderersByGameObjectId,
                skinnedMeshRenderersByGameObjectId,
                componentsByFileId,
                monoBehaviours,
                prefabInstances,
                sceneRootReferences,
                strippedProxies);

            bodyBuilder.Clear();
        }

        foreach (string line in File.ReadLines(filePath))
        {
            Match match = DocumentHeaderRegex.Match(line);
            if (match.Success)
            {
                FlushDocument();
                classId = int.Parse(match.Groups["classId"].Value, CultureInfo.InvariantCulture);
                fileId = long.Parse(match.Groups["fileId"].Value, CultureInfo.InvariantCulture);
                isStripped = match.Groups["stripped"].Success;
                continue;
            }

            if (classId.HasValue)
                bodyBuilder.AppendLine(line);
        }

        FlushDocument();
        var transformIdsByGameObjectId = transforms.Values
            .Where(static transform => !transform.IsStripped)
            .GroupBy(static transform => transform.GameObjectFileId)
            .ToDictionary(static group => group.Key, static group => group.First().FileId);

        return new ParsedSourceFile(
            gameObjects,
            transforms,
            transformIdsByGameObjectId,
            camerasByGameObjectId,
            lightsByGameObjectId,
            meshFiltersByGameObjectId,
            meshRenderersByGameObjectId,
            skinnedMeshRenderersByGameObjectId,
            componentsByFileId,
            monoBehaviours,
            prefabInstances,
            sceneRootReferences,
            strippedProxies);
    }

    private static void ProcessDocument(
        int classId,
        long fileId,
        bool isStripped,
        string body,
        int documentOrder,
        Dictionary<long, ParsedGameObject> gameObjects,
        Dictionary<long, ParsedTransform> transforms,
        Dictionary<long, ParsedCamera> camerasByGameObjectId,
        Dictionary<long, ParsedLight> lightsByGameObjectId,
        Dictionary<long, ParsedMeshFilter> meshFiltersByGameObjectId,
        Dictionary<long, ParsedMeshRenderer> meshRenderersByGameObjectId,
        Dictionary<long, ParsedSkinnedMeshRenderer> skinnedMeshRenderersByGameObjectId,
        Dictionary<long, ParsedSourceComponent> componentsByFileId,
        List<ParsedMonoBehaviour> monoBehaviours,
        List<ParsedPrefabInstance> prefabInstances,
        List<long> sceneRootReferences,
        Dictionary<long, ParsedStrippedProxy> strippedProxies)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(body));

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode rootNode || rootNode.Children.Count == 0)
            return;

        var documentEntry = rootNode.Children.First();
        string documentType = (documentEntry.Key as YamlScalarNode)?.Value ?? string.Empty;
        if (documentEntry.Value is not YamlMappingNode documentMapping)
            return;

        if (isStripped)
        {
            SourceReference correspondingSourceObject = ParseReference(GetNode(documentMapping, "m_CorrespondingSourceObject"));
            long prefabInstanceFileId = GetReferenceFileId(documentMapping, "m_PrefabInstance");
            SourceAssetObjectKind objectKind = documentType switch
            {
                "GameObject" => SourceAssetObjectKind.GameObject,
                "Transform" or "RectTransform" => SourceAssetObjectKind.Transform,
                "MeshRenderer" or "SkinnedMeshRenderer" => SourceAssetObjectKind.Renderer,
                _ => SourceAssetObjectKind.Component,
            };
            strippedProxies[fileId] = new ParsedStrippedProxy(
                fileId,
                correspondingSourceObject,
                prefabInstanceFileId,
                objectKind,
                documentOrder);
        }

        switch (documentType)
        {
            case "GameObject":
                gameObjects[fileId] = ParseGameObject(fileId, documentMapping, documentOrder, isStripped);
                break;
            case "Transform":
            case "RectTransform":
                transforms[fileId] = ParseTransform(fileId, documentMapping, documentOrder, isStripped);
                break;
            case "Camera":
                RegisterParsedComponent(ParseCamera(fileId, documentMapping, documentOrder), camerasByGameObjectId, componentsByFileId);
                break;
            case "Light":
                RegisterParsedComponent(ParseLight(fileId, documentMapping, documentOrder), lightsByGameObjectId, componentsByFileId);
                break;
            case "MeshFilter":
                RegisterParsedComponent(ParseMeshFilter(fileId, documentMapping, documentOrder), meshFiltersByGameObjectId, componentsByFileId);
                break;
            case "MeshRenderer":
                RegisterParsedComponent(ParseMeshRenderer(fileId, documentMapping, documentOrder), meshRenderersByGameObjectId, componentsByFileId);
                break;
            case "SkinnedMeshRenderer":
                RegisterParsedComponent(ParseSkinnedMeshRenderer(fileId, documentMapping, documentOrder), skinnedMeshRenderersByGameObjectId, componentsByFileId);
                break;
            case "MonoBehaviour":
                ParsedMonoBehaviour monoBehaviour = ParseMonoBehaviour(fileId, documentMapping, body, documentOrder, isStripped);
                monoBehaviours.Add(monoBehaviour);
                componentsByFileId[fileId] = monoBehaviour;
                break;
            case "PrefabInstance":
                prefabInstances.Add(ParsePrefabInstance(fileId, documentMapping, documentOrder));
                break;
            case "SceneRoots":
                sceneRootReferences.AddRange(ParseSceneRoots(documentMapping));
                break;
        }
    }

    private static ParsedGameObject ParseGameObject(
        long fileId,
        YamlMappingNode mapping,
        int documentOrder,
        bool isStripped)
    {
        string name = GetScalarString(mapping, "m_Name") ?? $"GameObject {fileId}";
        bool isActive = (GetScalarInt(mapping, "m_IsActive") ?? 1) != 0;
        int layer = GetScalarInt(mapping, "m_Layer") ?? 0;
        return new ParsedGameObject(fileId, name, isActive, layer, isStripped, documentOrder);
    }

    private static ParsedTransform ParseTransform(
        long fileId,
        YamlMappingNode mapping,
        int documentOrder,
        bool isStripped)
    {
        long gameObjectFileId = GetReferenceFileId(mapping, "m_GameObject");
        long parentFileId = GetReferenceFileId(mapping, "m_Father");
        Vector3 position = GetVector3(mapping, "m_LocalPosition", Vector3.Zero);
        Quaternion rotation = GetQuaternion(mapping, "m_LocalRotation", Quaternion.Identity);
        Vector3 scale = GetVector3(mapping, "m_LocalScale", Vector3.One);
        int? rootOrder = GetScalarInt(mapping, "m_RootOrder");
        List<long> childTransformFileIds = [];

        if (GetNode(mapping, "m_Children") is YamlSequenceNode childrenNode)
        {
            foreach (YamlNode childNode in childrenNode.Children)
            {
                SourceReference reference = ParseReference(childNode);
                if (reference.FileId != 0)
                    childTransformFileIds.Add(reference.FileId);
            }
        }

        return new ParsedTransform(
            fileId,
            gameObjectFileId,
            parentFileId,
            childTransformFileIds,
            position,
            rotation,
            scale,
            rootOrder,
            isStripped,
            documentOrder);
    }

    private static ParsedCamera ParseCamera(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        long gameObjectFileId = GetReferenceFileId(mapping, "m_GameObject");
        bool enabled = (GetScalarInt(mapping, "m_Enabled") ?? 1) != 0;
        float nearClipPlane = GetScalarFloat(mapping, "near clip plane") ?? 0.3f;
        float farClipPlane = GetScalarFloat(mapping, "far clip plane") ?? 1000.0f;
        float fieldOfView = GetScalarFloat(mapping, "field of view") ?? 60.0f;
        bool orthographic = (GetScalarInt(mapping, "orthographic") ?? 0) != 0;
        float orthographicSize = GetScalarFloat(mapping, "orthographic size") ?? 5.0f;
        return new ParsedCamera(fileId, gameObjectFileId, enabled, nearClipPlane, farClipPlane, fieldOfView, orthographic, orthographicSize, documentOrder);
    }

    private static ParsedLight ParseLight(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        long gameObjectFileId = GetReferenceFileId(mapping, "m_GameObject");
        bool enabled = (GetScalarInt(mapping, "m_Enabled") ?? 1) != 0;
        int lightType = GetScalarInt(mapping, "m_Type") ?? 1;
        Vector4 color = GetVector4(mapping, "m_Color", Vector4.One);
        float intensity = GetScalarFloat(mapping, "m_Intensity") ?? 1.0f;
        float range = GetScalarFloat(mapping, "m_Range") ?? 10.0f;
        float spotAngle = GetScalarFloat(mapping, "m_SpotAngle") ?? 30.0f;
        float innerSpotAngle = GetScalarFloat(mapping, "m_InnerSpotAngle") ?? MathF.Min(spotAngle, 21.80208f);
        bool castsShadows = (GetScalarInt(GetNode(mapping, "m_Shadows") as YamlMappingNode ?? [], "m_Type") ?? 0) != 0;
        return new ParsedLight(fileId, gameObjectFileId, enabled, lightType, color, intensity, range, spotAngle, innerSpotAngle, castsShadows, documentOrder);
    }

    private static ParsedMeshFilter ParseMeshFilter(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        long gameObjectFileId = GetReferenceFileId(mapping, "m_GameObject");
        SourceReference meshReference = ParseReference(GetNode(mapping, "m_Mesh"));
        return new ParsedMeshFilter(fileId, gameObjectFileId, meshReference, documentOrder);
    }

    private static ParsedMeshRenderer ParseMeshRenderer(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        long gameObjectFileId = GetReferenceFileId(mapping, "m_GameObject");
        bool enabled = (GetScalarInt(mapping, "m_Enabled") ?? 1) != 0;
        bool castShadows = (GetScalarInt(mapping, "m_CastShadows") ?? 1) != 0;
        bool receiveShadows = (GetScalarInt(mapping, "m_ReceiveShadows") ?? 1) != 0;
        List<SourceReference> materials = ParseReferenceSequence(GetNode(mapping, "m_Materials"));
        return new ParsedMeshRenderer(fileId, gameObjectFileId, enabled, castShadows, receiveShadows, materials, documentOrder);
    }

    private static ParsedSkinnedMeshRenderer ParseSkinnedMeshRenderer(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        long gameObjectFileId = GetReferenceFileId(mapping, "m_GameObject");
        bool enabled = (GetScalarInt(mapping, "m_Enabled") ?? 1) != 0;
        bool castShadows = (GetScalarInt(mapping, "m_CastShadows") ?? 1) != 0;
        bool receiveShadows = (GetScalarInt(mapping, "m_ReceiveShadows") ?? 1) != 0;
        SourceReference meshReference = ParseReference(GetNode(mapping, "m_Mesh"));
        List<SourceReference> materials = ParseReferenceSequence(GetNode(mapping, "m_Materials"));
        List<long> boneTransformFileIds = ParseReferenceSequence(GetNode(mapping, "m_Bones"))
            .Select(static reference => reference.FileId)
            .Where(static fileIdValue => fileIdValue != 0)
            .ToList();
        long rootBoneTransformFileId = ParseReference(GetNode(mapping, "m_RootBone")).FileId;
        return new ParsedSkinnedMeshRenderer(fileId, gameObjectFileId, enabled, castShadows, receiveShadows, materials, meshReference, boneTransformFileIds, rootBoneTransformFileId, documentOrder);
    }

    private static ParsedMonoBehaviour ParseMonoBehaviour(
        long fileId,
        YamlMappingNode mapping,
        string serializedYaml,
        int documentOrder,
        bool isStripped)
        => new(
            fileId,
            GetReferenceFileId(mapping, "m_GameObject"),
            (GetScalarInt(mapping, "m_Enabled") ?? 1) != 0,
            ParseReference(GetNode(mapping, "m_Script")),
            mapping,
            serializedYaml,
            isStripped,
            documentOrder);

    private static ParsedPrefabInstance ParsePrefabInstance(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        YamlMappingNode? modificationMapping = GetNode(mapping, "m_Modification") as YamlMappingNode;
        long transformParentFileId = modificationMapping is null ? 0 : GetReferenceFileId(modificationMapping, "m_TransformParent");
        var modifications = new List<PropertyModification>();
        List<SourceReference> removedComponents = modificationMapping is null
            ? []
            : ParseReferenceSequence(GetNode(modificationMapping, "m_RemovedComponents"), preferNestedTarget: true);
        List<SourceReference> removedGameObjects = modificationMapping is null
            ? []
            : ParseReferenceSequence(GetNode(modificationMapping, "m_RemovedGameObjects"), preferNestedTarget: true);
        List<AddedGameObjectDelta> addedGameObjects = modificationMapping is null
            ? []
            : ParseAddedGameObjectDeltas(GetNode(modificationMapping, "m_AddedGameObjects"));
        List<AddedComponentDelta> addedComponents = modificationMapping is null
            ? []
            : ParseAddedComponentDeltas(GetNode(modificationMapping, "m_AddedComponents"));

        if (modificationMapping is not null && GetNode(modificationMapping, "m_Modifications") is YamlSequenceNode modificationSequence)
        {
            foreach (YamlNode item in modificationSequence.Children)
            {
                if (item is not YamlMappingNode modificationNode)
                    continue;

                SourceReference target = ParseReference(GetNode(modificationNode, "target"));
                string propertyPath = GetScalarString(modificationNode, "propertyPath") ?? string.Empty;
                string? value = GetScalarString(modificationNode, "value");
                SourceReference objectReference = ParseReference(GetNode(modificationNode, "objectReference"));
                modifications.Add(new PropertyModification(target.FileId, propertyPath, value, objectReference));
            }
        }

        SourceReference sourcePrefab = ParseReference(GetNode(mapping, "m_SourcePrefab"));
        return new ParsedPrefabInstance(
            fileId,
            sourcePrefab,
            transformParentFileId,
            modifications,
            removedComponents,
            removedGameObjects,
            addedGameObjects,
            addedComponents,
            documentOrder);
    }

    private static List<long> ParseSceneRoots(YamlMappingNode mapping)
    {
        var roots = new List<long>();
        if (GetNode(mapping, "m_Roots") is not YamlSequenceNode rootSequence)
            return roots;

        foreach (YamlNode item in rootSequence.Children)
        {
            SourceReference reference = ParseReference(item);
            if (reference.FileId != 0)
                roots.Add(reference.FileId);
        }

        return roots;
    }

    private static string? ResolveAssetPath(ImportState state, string? guid)
        => string.IsNullOrWhiteSpace(guid) ? null : state.Context.Resolver.Resolve(guid);

    private static string? ExtractStringOverride(IEnumerable<PropertyModification> modifications, string propertyPath)
        => modifications.LastOrDefault(modification => string.Equals(modification.PropertyPath, propertyPath, StringComparison.Ordinal)).Value;

    private static void RegisterParsedComponent<T>(
        T component,
        Dictionary<long, T> byGameObjectId,
        Dictionary<long, ParsedSourceComponent> byFileId)
        where T : ParsedSourceComponent
    {
        byGameObjectId[component.GameObjectFileId] = component;
        byFileId[component.FileId] = component;
    }

    private static YamlNode? GetNode(YamlMappingNode mapping, string key)
    {
        foreach ((YamlNode yamlKey, YamlNode yamlValue) in mapping.Children)
        {
            if (string.Equals((yamlKey as YamlScalarNode)?.Value, key, StringComparison.Ordinal))
                return yamlValue;
        }

        return null;
    }

    private static string? GetScalarString(YamlMappingNode mapping, string key)
        => (GetNode(mapping, key) as YamlScalarNode)?.Value;

    private static int? GetScalarInt(YamlMappingNode mapping, string key)
    {
        string? value = GetScalarString(mapping, key);
        return TryParseInt(value, out int result) ? result : null;
    }

    private static bool TryParseInt(string? value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryParseFloat(string? value, out float result)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static bool TryParseBool(string? value, out bool result)
    {
        if (TryParseInt(value, out int numericValue))
        {
            result = numericValue != 0;
            return true;
        }

        return bool.TryParse(value, out result);
    }

    private static long GetReferenceFileId(YamlMappingNode mapping, string key)
        => ParseReference(GetNode(mapping, key)).FileId;

    private static List<SourceReference> ParseReferenceSequence(YamlNode? node, bool preferNestedTarget = false)
    {
        if (node is not YamlSequenceNode sequenceNode)
            return [];

        var references = new List<SourceReference>(sequenceNode.Children.Count);
        foreach (YamlNode child in sequenceNode.Children)
        {
            SourceReference reference = preferNestedTarget
                ? ParseNestedDeltaReference(child)
                : ParseReference(child);

            if (reference.FileId != 0 || !string.IsNullOrWhiteSpace(reference.Guid))
                references.Add(reference);
        }

        return references;
    }

    private static List<AddedGameObjectDelta> ParseAddedGameObjectDeltas(YamlNode? node)
    {
        if (node is not YamlSequenceNode sequenceNode)
            return [];

        var deltas = new List<AddedGameObjectDelta>(sequenceNode.Children.Count);
        foreach (YamlNode child in sequenceNode.Children)
        {
            if (child is not YamlMappingNode mapping)
                continue;

            SourceReference target = ParseNestedReference(mapping, "targetCorrespondingSourceObject", "target");
            SourceReference addedObject = ParseNestedReference(mapping, "addedObject", "objectReference", "instance");
            int? insertIndex = GetScalarInt(mapping, "insertIndex");
            deltas.Add(new AddedGameObjectDelta(target, addedObject, insertIndex));
        }

        return deltas;
    }

    private static List<AddedComponentDelta> ParseAddedComponentDeltas(YamlNode? node)
    {
        if (node is not YamlSequenceNode sequenceNode)
            return [];

        var deltas = new List<AddedComponentDelta>(sequenceNode.Children.Count);
        foreach (YamlNode child in sequenceNode.Children)
        {
            if (child is not YamlMappingNode mapping)
                continue;

            SourceReference target = ParseNestedReference(mapping, "targetCorrespondingSourceObject", "target");
            SourceReference addedObject = ParseNestedReference(mapping, "addedObject", "objectReference", "instance");
            int? insertIndex = GetScalarInt(mapping, "insertIndex");
            deltas.Add(new AddedComponentDelta(target, addedObject, insertIndex));
        }

        return deltas;
    }

    private static SourceReference ParseNestedDeltaReference(YamlNode? node)
    {
        if (node is not YamlMappingNode mapping)
            return ParseReference(node);

        SourceReference nested = ParseNestedReference(mapping, "targetCorrespondingSourceObject", "target", "addedObject", "objectReference");
        return nested.FileId != 0 || !string.IsNullOrWhiteSpace(nested.Guid)
            ? nested
            : ParseReference(mapping);
    }

    private static SourceReference ParseNestedReference(YamlMappingNode mapping, params string[] keys)
    {
        foreach (string key in keys)
        {
            SourceReference nested = ParseReference(GetNode(mapping, key));
            if (nested.FileId != 0 || !string.IsNullOrWhiteSpace(nested.Guid))
                return nested;
        }

        return default;
    }

    private static SourceReference ParseReference(YamlNode? node)
    {
        if (node is not YamlMappingNode mapping)
            return default;

        long fileId = 0;
        string? guid = null;
        int? type = null;

        string? fileIdValue = GetScalarString(mapping, "fileID");
        if (!string.IsNullOrWhiteSpace(fileIdValue))
            long.TryParse(fileIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);

        guid = GetScalarString(mapping, "guid");

        string? typeValue = GetScalarString(mapping, "type");
        if (TryParseInt(typeValue, out int parsedType))
            type = parsedType;

        return new SourceReference(fileId, guid, type);
    }

    private static Vector3 GetVector3(YamlMappingNode mapping, string key, Vector3 fallback)
    {
        if (GetNode(mapping, key) is not YamlMappingNode vectorMapping)
            return fallback;

        return new Vector3(
            GetScalarFloat(vectorMapping, "x") ?? fallback.X,
            GetScalarFloat(vectorMapping, "y") ?? fallback.Y,
            GetScalarFloat(vectorMapping, "z") ?? fallback.Z);
    }

    private static Vector4 GetVector4(YamlMappingNode mapping, string key, Vector4 fallback)
    {
        if (GetNode(mapping, key) is not YamlMappingNode vectorMapping)
            return fallback;

        return new Vector4(
            GetScalarFloat(vectorMapping, "r") ?? GetScalarFloat(vectorMapping, "x") ?? fallback.X,
            GetScalarFloat(vectorMapping, "g") ?? GetScalarFloat(vectorMapping, "y") ?? fallback.Y,
            GetScalarFloat(vectorMapping, "b") ?? GetScalarFloat(vectorMapping, "z") ?? fallback.Z,
            GetScalarFloat(vectorMapping, "a") ?? GetScalarFloat(vectorMapping, "w") ?? fallback.W);
    }

    private static Quaternion GetQuaternion(YamlMappingNode mapping, string key, Quaternion fallback)
    {
        if (GetNode(mapping, key) is not YamlMappingNode quaternionMapping)
            return fallback;

        var quaternion = new Quaternion(
            GetScalarFloat(quaternionMapping, "x") ?? fallback.X,
            GetScalarFloat(quaternionMapping, "y") ?? fallback.Y,
            GetScalarFloat(quaternionMapping, "z") ?? fallback.Z,
            GetScalarFloat(quaternionMapping, "w") ?? fallback.W);

        return NormalizeQuaternion(quaternion);
    }

    private static float? GetScalarFloat(YamlMappingNode mapping, string key)
    {
        string? value = GetScalarString(mapping, key);
        return TryParseFloat(value, out float result) ? result : null;
    }

    private static Vector3 ConvertPosition(Vector3 sourcePosition)
        => new(sourcePosition.X, sourcePosition.Y, -sourcePosition.Z);

    private static Quaternion ConvertRotation(Quaternion sourceRotation)
        => NormalizeQuaternion(new Quaternion(-sourceRotation.X, -sourceRotation.Y, sourceRotation.Z, sourceRotation.W));

    private static Quaternion NormalizeQuaternion(Quaternion quaternion)
        => quaternion.LengthSquared() > 0.000001f ? Quaternion.Normalize(quaternion) : Quaternion.Identity;

    private sealed class ImportState(SourceProjectImportContext context)
    {
        public SourceProjectImportContext Context { get; } = context;
        public string EntryFilePath => Context.EntrySourcePath;
        public string ProjectRoot => Context.ProjectRoot;
        public HashSet<string> ActiveImports => Context.ActiveImports;
    }

    private sealed class ImportedHierarchy(string sourcePath)
    {
        public string SourcePath { get; } = sourcePath;
        public string? SourceGuid { get; set; }
        public Dictionary<long, SceneNode> NodesByTransformId { get; } = [];
        public Dictionary<long, SceneNode> NodesByGameObjectId { get; } = [];
        public Dictionary<long, XRComponent> ComponentsByFileId { get; } = [];
        public Dictionary<SourceAssetIdentity, SceneNode> NodesByIdentity { get; } = [];
        public Dictionary<SourceAssetIdentity, XRComponent> ComponentsByIdentity { get; } = [];
        public Dictionary<long, int> TransformSortOrders { get; } = [];
        public HashSet<long> ExcludedRootTransformIds { get; } = [];
        public List<ImportedRootEntry> RootEntries { get; } = [];

        public void SetTransformSortOrder(long transformFileId, int sortOrder)
        {
            TransformSortOrders[transformFileId] = sortOrder;
            foreach (ImportedRootEntry rootEntry in RootEntries)
            {
                if (rootEntry.TransformFileId == transformFileId)
                    rootEntry.SortOrder = sortOrder;
            }
        }

        public void SortRoots()
        {
            RootEntries.Sort(static (left, right) =>
            {
                int orderComparison = left.SortOrder.CompareTo(right.SortOrder);
                return orderComparison != 0
                    ? orderComparison
                    : left.DiscoveryOrder.CompareTo(right.DiscoveryOrder);
            });
        }
    }

    private sealed class ImportedRootEntry(SceneNode node, long? transformFileId, int sortOrder, int discoveryOrder)
    {
        public SceneNode Node { get; } = node;
        public long? TransformFileId { get; } = transformFileId;
        public int SortOrder { get; set; } = sortOrder;
        public int DiscoveryOrder { get; } = discoveryOrder;
    }

    private sealed record ParsedSourceFile(
        Dictionary<long, ParsedGameObject> GameObjects,
        Dictionary<long, ParsedTransform> Transforms,
        Dictionary<long, long> TransformIdsByGameObjectId,
        Dictionary<long, ParsedCamera> CamerasByGameObjectId,
        Dictionary<long, ParsedLight> LightsByGameObjectId,
        Dictionary<long, ParsedMeshFilter> MeshFiltersByGameObjectId,
        Dictionary<long, ParsedMeshRenderer> MeshRenderersByGameObjectId,
        Dictionary<long, ParsedSkinnedMeshRenderer> SkinnedMeshRenderersByGameObjectId,
        Dictionary<long, ParsedSourceComponent> ComponentsByFileId,
        List<ParsedMonoBehaviour> MonoBehaviours,
        List<ParsedPrefabInstance> PrefabInstances,
        List<long> SceneRootReferences,
        Dictionary<long, ParsedStrippedProxy> StrippedProxies);

    private sealed record ParsedGameObject(
        long FileId,
        string Name,
        bool IsActive,
        int Layer,
        bool IsStripped,
        int DocumentOrder);

    private sealed record ParsedTransform(
        long FileId,
        long GameObjectFileId,
        long ParentTransformFileId,
        List<long> ChildTransformFileIds,
        Vector3 LocalPosition,
        Quaternion LocalRotation,
        Vector3 LocalScale,
        int? RootOrder,
        bool IsStripped,
        int DocumentOrder);

    private abstract record ParsedSourceComponent(long FileId, long GameObjectFileId, bool Enabled, int DocumentOrder);

    private sealed record ParsedCamera(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        float NearClipPlane,
        float FarClipPlane,
        float FieldOfView,
        bool Orthographic,
        float OrthographicSize,
        int DocumentOrder)
        : ParsedSourceComponent(FileId, GameObjectFileId, Enabled, DocumentOrder);

    private sealed record ParsedLight(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        int LightType,
        Vector4 Color,
        float Intensity,
        float Range,
        float SpotAngle,
        float InnerSpotAngle,
        bool CastsShadows,
        int DocumentOrder)
        : ParsedSourceComponent(FileId, GameObjectFileId, Enabled, DocumentOrder);

    private sealed record ParsedMeshFilter(
        long FileId,
        long GameObjectFileId,
        SourceReference MeshReference,
        int DocumentOrder)
        : ParsedSourceComponent(FileId, GameObjectFileId, true, DocumentOrder);

    private abstract record ParsedRendererComponent(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        bool CastShadows,
        bool ReceiveShadows,
        List<SourceReference> Materials,
        int DocumentOrder)
        : ParsedSourceComponent(FileId, GameObjectFileId, Enabled, DocumentOrder);

    private sealed record ParsedMeshRenderer(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        bool CastShadows,
        bool ReceiveShadows,
        List<SourceReference> Materials,
        int DocumentOrder)
        : ParsedRendererComponent(FileId, GameObjectFileId, Enabled, CastShadows, ReceiveShadows, Materials, DocumentOrder);

    private sealed record ParsedSkinnedMeshRenderer(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        bool CastShadows,
        bool ReceiveShadows,
        List<SourceReference> Materials,
        SourceReference MeshReference,
        List<long> BoneTransformFileIds,
        long RootBoneTransformFileId,
        int DocumentOrder)
        : ParsedRendererComponent(FileId, GameObjectFileId, Enabled, CastShadows, ReceiveShadows, Materials, DocumentOrder);

    private sealed record ParsedMonoBehaviour(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        SourceReference Script,
        YamlMappingNode SerializedFields,
        string SerializedYaml,
        bool IsStripped,
        int DocumentOrder)
        : ParsedSourceComponent(FileId, GameObjectFileId, Enabled, DocumentOrder);

    private sealed record ParsedPrefabInstance(
        long FileId,
        SourceReference SourcePrefab,
        long TransformParentFileId,
        List<PropertyModification> Modifications,
        List<SourceReference> RemovedComponents,
        List<SourceReference> RemovedGameObjects,
        List<AddedGameObjectDelta> AddedGameObjects,
        List<AddedComponentDelta> AddedComponents,
        int DocumentOrder);

    private sealed record ParsedStrippedProxy(
        long LocalFileId,
        SourceReference CorrespondingSourceObject,
        long PrefabInstanceFileId,
        SourceAssetObjectKind ObjectKind,
        int DocumentOrder);

    private readonly record struct SourceReference(long FileId, string? Guid, int? Type);

    private readonly record struct AddedGameObjectDelta(SourceReference TargetCorrespondingSourceObject, SourceReference AddedObject, int? InsertIndex);

    private readonly record struct AddedComponentDelta(SourceReference TargetCorrespondingSourceObject, SourceReference AddedObject, int? InsertIndex);

    private readonly record struct PropertyModification(long TargetFileId, string PropertyPath, string? Value, SourceReference ObjectReference);
}
