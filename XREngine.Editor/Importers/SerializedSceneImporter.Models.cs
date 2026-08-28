using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Importers.SourceToon;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    private static ImportedHierarchy ImportModelHierarchy(
        string modelPath,
        string? sourceGuid,
        ImportState state)
    {
        state.Context.CancellationToken.ThrowIfCancellationRequested();
        string metaPath = modelPath + ".meta";
        SerializedModelImporterDocument metadata = File.Exists(metaPath)
            ? SerializedModelImporterDocumentParser.ParseFile(metaPath)
            : new SerializedModelImporterDocument { SourceMetaPath = metaPath };

        if (metadata.FileIdsGeneration != 2)
        {
            throw new SourceVisualImportException(
                $"Model '{modelPath}' uses unsupported Unity fileIdsGeneration '{metadata.FileIdsGeneration}'. " +
                "Only deterministic generation 2 correspondence is supported.");
        }

        Dictionary<string, XRMaterial> externalMaterials = ImportExternalMaterialRemaps(metadata, state);
        ModelImportOptions options = CreateSerializedModelImportOptions(metadata, externalMaterials);

        using var importer = new ModelAssetImporter(modelPath, onCompleted: null, materialFactory: null)
        {
            ImportOptions = options,
        };
        Func<string, XRTexture2D> defaultTextureFactory = importer.MakeTextureAction;
        importer.MakeTextureAction = path => ResolveModelTexture(path, defaultTextureFactory, state);
        importer.MakeMaterialAction = (textureList, textureSlots, materialName) =>
        {
            if (externalMaterials.TryGetValue(materialName, out XRMaterial? remapped))
                return remapped;

            return ModelAssetImporter.MakeMaterialDeferred(textureList, textureSlots, materialName);
        };

        state.Context.Progress?.Invoke(0.25f, $"Importing model {Path.GetFileName(modelPath)}");
        SceneNode? root = importer.Import(
            options.ImportSteps,
            preservePivots: options.FbxPivotPolicy == XREngine.Fbx.FbxPivotImportPolicy.PreservePivotSemantics,
            removeAssimpFBXNodes: options.CollapseGeneratedFbxHelperNodes,
            scaleConversion: options.ScaleConversion,
            zUp: options.ZUp,
            multiThread: options.MultiThread,
            processMeshesAsynchronously: false,
            batchSubmeshAddsDuringAsyncImport: true,
            cancellationToken: state.Context.CancellationToken,
            onProgress: progress => state.Context.Progress?.Invoke(
                0.25f + (0.45f * progress),
                $"Importing model {Path.GetFileName(modelPath)}"));
        if (root is null)
            throw new SourceVisualImportException($"Model importer returned no hierarchy for '{modelPath}'.");

        root = CollapseAssimpSyntheticRoot(root, modelPath, metadata.PreserveHierarchy);
        ApplySourceImportedSkeletonBindPose(root, metadata, state);
        if (metadata.SortHierarchyByName)
            SortHierarchyByName(root);

        // Do not cook this imported model here. A Unity prefab can compose this
        // hierarchy with built-in and .asset meshes and then apply topology
        // changes from prefab overrides. The completed prefab hierarchy owns one
        // cook immediately before it is published, so every mesh receives an
        // identity from its final Unity placement.

        var hierarchy = new ImportedHierarchy(modelPath)
        {
            SourceGuid = sourceGuid,
        };
        hierarchy.RootEntries.Add(new ImportedRootEntry(root, transformFileId: null, sortOrder: 0, discoveryOrder: 0));
        // Unity's generation-2 ModelImporter identifies the generated model
        // root as "//RootNode/root". Assimp exposes its own synthetic
        // "RootNode" instead, so using that display hierarchy verbatim shifts
        // every GameObject, Transform, and renderer fileID.
        IndexModelHierarchy(root, "//RootNode/root", hierarchy, state);
        RegisterSerializedAnimatorRecord(
            root,
            "//RootNode/root",
            metadata,
            hierarchy,
            state);
        RegisterIdentityMappings(hierarchy);
        state.Context.MarkOutcome(modelPath, SourceImportConversionOutcome.Converted);
        if (File.Exists(metaPath))
            state.Context.MarkOutcome(metaPath, SourceImportConversionOutcome.Converted);

        state.Context.AddDiagnostic(
            "UNITYMODEL0002",
            SourceImportDiagnosticSeverity.Info,
            SourceImportDiagnosticCategory.ModelIdentity,
            $"Imported model with Unity fileIDsGeneration 2, animationType={metadata.AnimationType}, " +
            $"importAnimation={metadata.ImportAnimation}, importBlendShapes={metadata.ImportBlendShapes}, " +
            $"Assimp FBX compatibility backend, and {metadata.ExternalMaterialRemaps.Count} external material remaps.",
            modelPath);
        return hierarchy;
    }

    internal static ModelImportOptions CreateSerializedModelImportOptions(
        SerializedModelImporterDocument metadata,
        IReadOnlyDictionary<string, XRMaterial> externalMaterials)
        => new()
        {
            // Unity's generated hierarchy/fileID correspondence is currently validated against
            // the mature Assimp FBX path. The native FBX path retains a much larger sparse
            // morph/skin working set for production avatar files and is not yet a safe choice
            // for editor-side Unity prefab composition.
            FbxBackend = FbxImportBackend.Assimp,
            ScaleConversion = metadata.GlobalScale,
            ZUp = false,
            // Unity-authored assets face +Z while XRENGINE faces -Z. Assimp applies this
            // reflection coherently to vertices, hierarchy transforms, and inverse bind
            // matrices. Reverse winding in the same import step so reflected triangles
            // remain front-facing. Unity YAML transforms are converted separately because
            // they do not pass through the model importer.
            MakeLeftHanded = true,
            FlipWindingOrder = true,
            FbxPivotPolicy = metadata.BakeAxisConversion
                ? XREngine.Fbx.FbxPivotImportPolicy.BakeIntoLocalTransform
                : XREngine.Fbx.FbxPivotImportPolicy.PreservePivotSemantics,
            CollapseGeneratedFbxHelperNodes = !metadata.PreserveHierarchy,
            // Assimp's graph optimizer removes empty FBX grouping nodes such as
            // "Meshes/Tops". Unity retains those nodes even when
            // ModelImporter.preserveHierarchy is disabled, and includes them in
            // generation-2 GameObject/Transform/renderer fileIDs.
            OptimizeGraph = false,
            OptimizeMeshes = false,
            DeferMeshletCookingUntilPostNormalization = true,
            ProcessMeshesAsynchronously = false,
            GenerateMeshRenderersAsync = false,
            MaterialRemap = externalMaterials.ToDictionary(
                static pair => pair.Key,
                static pair => (XRMaterial?)pair.Value,
                StringComparer.Ordinal),
        };

    private static Dictionary<string, XRMaterial> ImportExternalMaterialRemaps(
        SerializedModelImporterDocument metadata,
        ImportState state)
    {
        var materials = new Dictionary<string, XRMaterial>(StringComparer.Ordinal);
        for (int remapIndex = 0; remapIndex < metadata.ExternalMaterialRemaps.Count; remapIndex++)
        {
            SourceExternalMaterialRemap remap = metadata.ExternalMaterialRemaps[remapIndex];
            state.Context.Progress?.Invoke(
                0.08f + (0.14f * remapIndex / Math.Max(1, metadata.ExternalMaterialRemaps.Count)),
                $"Converting FBX material remap {remapIndex + 1}/{metadata.ExternalMaterialRemaps.Count}: {remap.SourceMaterialName}");
            string? materialPath = ResolveAssetPath(state, remap.TargetMaterial.Guid);
            if (string.IsNullOrWhiteSpace(materialPath) || !File.Exists(materialPath))
            {
                throw new SourceVisualImportException(
                    $"Required FBX material remap '{remap.SourceMaterialName}' ({remap.TargetMaterial.Guid}) " +
                    $"from '{metadata.SourceMetaPath}' could not be resolved.");
            }

            XRMaterial material = state.Context.GetOrAddCached(
                materialPath,
                () => SerializedMaterialImporter.ImportWithReport(materialPath, state.Context).Material
                    ?? throw new SourceVisualImportException($"Unity material importer returned no material for '{materialPath}'."));
            materials[remap.SourceMaterialName] = material;
            state.Context.MarkOutcome(materialPath, GetMaterialOutcome(material));
        }
        state.Context.Progress?.Invoke(0.22f, "FBX external material remaps converted");

        return materials;
    }

    private static XRTexture2D ResolveModelTexture(
        string texturePath,
        Func<string, XRTexture2D> defaultFactory,
        ImportState state)
    {
        string normalized = Path.GetFullPath(texturePath);
        return state.Context.GetOrAddCached(normalized, () =>
        {
            XRTexture2D texture = defaultFactory(normalized);
            state.Context.MarkOutcome(normalized, SourceImportConversionOutcome.Converted);
            return texture;
        });
    }

    private static SourceImportConversionOutcome GetMaterialOutcome(XRMaterial material)
    {
        return MaterialConversionReportRegistry.Instance.TryGet(material, out MaterialConversionReport? report) &&
               report.Outcome == EMaterialConversionOutcome.ConvertedToSourceToon
            ? SourceImportConversionOutcome.Downgraded
            : SourceImportConversionOutcome.Converted;
    }

    private static void IndexModelHierarchy(
        SceneNode node,
        string sourcePath,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        long gameObjectFileId = SerializedModelFileId.ForGameObject(sourcePath);
        long transformFileId = SerializedModelFileId.ForTransform(sourcePath);
        AddUniqueNodeIdentity(hierarchy.NodesByGameObjectId, gameObjectFileId, node, sourcePath, "GameObject", state);
        AddUniqueNodeIdentity(hierarchy.NodesByTransformId, transformFileId, node, sourcePath, "Transform", state);
        hierarchy.TransformSortOrders[transformFileId] = node.Parent?.Transform.Children.IndexOf(node.Transform) ?? 0;

        foreach (XRComponent component in node.Components)
        {
            string sourceType = component switch
            {
                ModelComponent model when RequiresSkinnedMeshRenderer(model) => "SkinnedMeshRenderer",
                ModelComponent => "MeshRenderer",
                _ => component.GetType().Name,
            };
            long componentFileId = SerializedModelFileId.ForComponent(sourceType, sourcePath);
            if (!hierarchy.ComponentsByFileId.TryAdd(componentFileId, component))
            {
                state.Context.AddDiagnostic(
                    "UNITYMODEL0003",
                    SourceImportDiagnosticSeverity.Error,
                    SourceImportDiagnosticCategory.ModelIdentity,
                    $"Unity {sourceType} fileID collision at '{sourcePath}' ({componentFileId}).",
                    hierarchy.SourcePath);
            }
        }

        var siblingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SceneNode child in node.Transform.Children
            .Select(static transform => transform.SceneNode)
            .Where(static child => child is not null)
            .Cast<SceneNode>())
        {
            string name = child.Name ?? SceneNode.DefaultName;
            siblingCounts.TryGetValue(name, out int occurrence);
            siblingCounts[name] = occurrence + 1;
            string segment = occurrence == 0 ? name : $"{name} {occurrence}";
            if (occurrence > 0)
                child.Name = segment;
            IndexModelHierarchy(child, $"{sourcePath}/{segment}", hierarchy, state);
        }
    }

    private static void RegisterSerializedAnimatorRecord(
        SceneNode owner,
        string sourcePath,
        SerializedModelImporterDocument metadata,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        long fileId = SerializedModelFileId.ForComponent("Animator", sourcePath);
        var record = new SerializedAnimatorRecord
        {
            Identity = new SourceAssetIdentity
            {
                AssetGuid = hierarchy.SourceGuid ?? string.Empty,
                LocalFileId = fileId,
                ObjectKind = SourceAssetObjectKind.Component,
            },
            Enabled = metadata.ImportAnimation,
            HasTransformHierarchy = true,
        };

        hierarchy.SerializedAnimatorsByFileId[fileId] = record;
        hierarchy.SerializedAnimatorOwners[record] = owner;
        state.Context.RegisterAnimator(owner, record);
        state.Context.AddDiagnostic(
            "UNITYAVATAR0002",
            SourceImportDiagnosticSeverity.Info,
            SourceImportDiagnosticCategory.AvatarComponent,
            "Animator controller, update mode, and culling mode were retained in the import manifest only. " +
            "No runtime component was attached because the controller has not been compiled into a native animation state machine.",
            hierarchy.SourcePath,
            identity: record.Identity);
    }

    private static void AddUniqueNodeIdentity(
        Dictionary<long, SceneNode> destination,
        long fileId,
        SceneNode node,
        string path,
        string kind,
        ImportState state)
    {
        if (destination.TryAdd(fileId, node))
            return;

        state.Context.AddDiagnostic(
            "UNITYMODEL0004",
            SourceImportDiagnosticSeverity.Error,
            SourceImportDiagnosticCategory.ModelIdentity,
            $"Unity {kind} fileID collision for hierarchy path '{path}' ({fileId}).",
            state.EntryFilePath);
    }

    private static bool RequiresSkinnedMeshRenderer(ModelComponent component)
        => component.Model?.Meshes
            .SelectMany(static subMesh => subMesh.LODs)
            .Any(static lod => lod.Mesh is { } mesh && (mesh.HasSkinning || mesh.HasBlendshapes)) == true;

    private static SceneNode CollapseAssimpSyntheticRoot(
        SceneNode root,
        string modelPath,
        bool preserveHierarchy)
    {
        if (root.Transform.Children.Count != 1 ||
            root.Transform.Children[0].SceneNode is not SceneNode syntheticRoot ||
            !string.Equals(syntheticRoot.Name, "RootNode", StringComparison.Ordinal) ||
            root.Components.Count != 0)
        {
            return root;
        }

        if (syntheticRoot.Transform is not Transform transform ||
            !IsIdentityTransform(transform))
        {
            throw new SourceVisualImportException(
                $"Assimp produced a non-identity synthetic RootNode while importing '{modelPath}'. " +
                "The Unity generation-2 hierarchy cannot be flattened without applying an additional coordinate conversion.");
        }

        SceneNode importedRoot = syntheticRoot;
        if (!preserveHierarchy &&
            syntheticRoot.Components.Count == 0 &&
            syntheticRoot.Transform.Children.Count == 1 &&
            syntheticRoot.Transform.Children[0].SceneNode is SceneNode singleAuthoredRoot)
        {
            // With preserveHierarchy disabled, Unity folds a sole authored FBX
            // root into the generated model root. This is observable on models
            // whose only mesh is a blendshape renderer attached to that root.
            importedRoot = singleAuthoredRoot;
        }

        // ModelAssetImporter expresses imported submesh bounds relative to its outer file wrapper.
        // That wrapper is removed here for Unity's generation-2 hierarchy, so every surviving
        // submesh must point at the replacement root rather than a detached random transform.
        RebindCollapsedRootTransform(importedRoot, root.Transform, importedRoot.Transform);
        if (!ReferenceEquals(importedRoot, syntheticRoot))
            RebindCollapsedRootTransform(importedRoot, syntheticRoot.Transform, importedRoot.Transform);

        string? importedRootName = root.Name;
        importedRoot.Parent = null;
        importedRoot.Name = importedRootName;
        return importedRoot;
    }

    private static void RebindCollapsedRootTransform(
        SceneNode node,
        TransformBase collapsedRoot,
        TransformBase importedRoot)
    {
        foreach (ModelComponent component in node.Components.OfType<ModelComponent>())
        {
            if (component.Model is not Model model)
                continue;

            foreach (SubMesh subMesh in model.Meshes)
            {
                if (ReferenceEquals(subMesh.RootTransform, collapsedRoot))
                    subMesh.RootTransform = importedRoot;
                if (ReferenceEquals(subMesh.RootBone, collapsedRoot))
                    subMesh.RootBone = importedRoot;
            }
        }

        foreach (TransformBase childTransform in node.Transform.Children)
        {
            if (childTransform.SceneNode is SceneNode child)
                RebindCollapsedRootTransform(child, collapsedRoot, importedRoot);
        }
    }

    private static bool IsIdentityTransform(Transform transform)
    {
        const float epsilon = 1.0e-5f;
        return MathF.Abs(transform.Translation.X) <= epsilon &&
               MathF.Abs(transform.Translation.Y) <= epsilon &&
               MathF.Abs(transform.Translation.Z) <= epsilon &&
               MathF.Abs(transform.Rotation.X) <= epsilon &&
               MathF.Abs(transform.Rotation.Y) <= epsilon &&
               MathF.Abs(transform.Rotation.Z) <= epsilon &&
               MathF.Abs(transform.Rotation.W - 1.0f) <= epsilon &&
               MathF.Abs(transform.Scale.X - 1.0f) <= epsilon &&
               MathF.Abs(transform.Scale.Y - 1.0f) <= epsilon &&
               MathF.Abs(transform.Scale.Z - 1.0f) <= epsilon;
    }

    private static void SortHierarchyByName(SceneNode node)
    {
        TransformBase[] ordered =
        [
            .. node.Transform.Children
                .OrderBy(static transform => transform.SceneNode?.Name, StringComparer.Ordinal)
                .ThenBy(static transform => transform.SceneNode?.ID),
        ];
        SetOrderedChildren(node, ordered);
        foreach (TransformBase child in ordered)
        {
            if (child.SceneNode is SceneNode childNode)
                SortHierarchyByName(childNode);
        }
    }
}
