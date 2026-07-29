using Assimp;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene.Importers.Poiyomi;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

internal static partial class UnitySceneImporter
{
    private static ImportedHierarchy ImportModelHierarchy(
        string modelPath,
        string? sourceGuid,
        ImportState state)
    {
        state.Context.CancellationToken.ThrowIfCancellationRequested();
        string metaPath = modelPath + ".meta";
        UnityModelImporterDocument metadata = File.Exists(metaPath)
            ? UnityModelImporterDocumentParser.ParseFile(metaPath)
            : new UnityModelImporterDocument { SourceMetaPath = metaPath };

        if (metadata.FileIdsGeneration != 2)
        {
            throw new UnityVisualImportException(
                $"Model '{modelPath}' uses unsupported Unity fileIdsGeneration '{metadata.FileIdsGeneration}'. " +
                "Only deterministic generation 2 correspondence is supported.");
        }

        Dictionary<string, XRMaterial> externalMaterials = ImportExternalMaterialRemaps(metadata, state);
        var options = new ModelImportOptions
        {
            // Unity's generated hierarchy/fileID correspondence is currently validated against
            // the mature Assimp FBX path. The native FBX path retains a much larger sparse
            // morph/skin working set for production avatar files and is not yet a safe choice
            // for editor-side Unity prefab composition.
            FbxBackend = FbxImportBackend.Assimp,
            ScaleConversion = metadata.GlobalScale,
            ZUp = false,
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
            ProcessMeshesAsynchronously = false,
            GenerateMeshRenderersAsync = false,
            MaterialRemap = externalMaterials.ToDictionary(
                static pair => pair.Key,
                static pair => (XRMaterial?)pair.Value,
                StringComparer.Ordinal),
        };

        using var importer = new ModelImporter(modelPath, onCompleted: null, materialFactory: null)
        {
            ImportOptions = options,
        };
        Func<string, XRTexture2D> defaultTextureFactory = importer.MakeTextureAction;
        importer.MakeTextureAction = path => ResolveModelTexture(path, defaultTextureFactory, state);
        importer.MakeMaterialAction = (textureList, textureSlots, materialName) =>
        {
            if (externalMaterials.TryGetValue(materialName, out XRMaterial? remapped))
                return remapped;

            return ModelImporter.MakeMaterialDeferred(textureList, textureSlots, materialName);
        };

        state.Context.Progress?.Invoke(0.25f, $"Importing model {Path.GetFileName(modelPath)}");
        SceneNode? root = importer.Import(
            options.PostProcessSteps,
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
            throw new UnityVisualImportException($"Model importer returned no hierarchy for '{modelPath}'.");

        root = CollapseAssimpSyntheticRoot(root, modelPath, metadata.PreserveHierarchy);
        if (metadata.SortHierarchyByName)
            SortHierarchyByName(root);

        UnityAnimatorImportMetadataComponent? animatorMetadata = root.TryGetComponent<UnityAnimatorImportMetadataComponent>(
            out UnityAnimatorImportMetadataComponent? existingAnimatorMetadata)
            ? existingAnimatorMetadata
            : root.AddComponent<UnityAnimatorImportMetadataComponent>();
        if (animatorMetadata is not null)
        {
            animatorMetadata.IsActive = metadata.ImportAnimation;
            animatorMetadata.HasTransformHierarchy = true;
        }

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
        RegisterIdentityMappings(hierarchy);
        state.Context.MarkOutcome(modelPath, UnityImportConversionOutcome.Converted);
        if (File.Exists(metaPath))
            state.Context.MarkOutcome(metaPath, UnityImportConversionOutcome.Converted);

        state.Context.AddDiagnostic(
            "UNITYMODEL0002",
            UnityImportDiagnosticSeverity.Info,
            UnityImportDiagnosticCategory.ModelIdentity,
            $"Imported model with Unity fileIDsGeneration 2, animationType={metadata.AnimationType}, " +
            $"importAnimation={metadata.ImportAnimation}, importBlendShapes={metadata.ImportBlendShapes}, " +
            $"Assimp FBX compatibility backend, and {metadata.ExternalMaterialRemaps.Count} external material remaps.",
            modelPath);
        return hierarchy;
    }

    private static Dictionary<string, XRMaterial> ImportExternalMaterialRemaps(
        UnityModelImporterDocument metadata,
        ImportState state)
    {
        var materials = new Dictionary<string, XRMaterial>(StringComparer.Ordinal);
        for (int remapIndex = 0; remapIndex < metadata.ExternalMaterialRemaps.Count; remapIndex++)
        {
            UnityExternalMaterialRemap remap = metadata.ExternalMaterialRemaps[remapIndex];
            state.Context.Progress?.Invoke(
                0.08f + (0.14f * remapIndex / Math.Max(1, metadata.ExternalMaterialRemaps.Count)),
                $"Converting FBX material remap {remapIndex + 1}/{metadata.ExternalMaterialRemaps.Count}: {remap.SourceMaterialName}");
            string? materialPath = ResolveAssetPath(state, remap.TargetMaterial.Guid);
            if (string.IsNullOrWhiteSpace(materialPath) || !File.Exists(materialPath))
            {
                throw new UnityVisualImportException(
                    $"Required FBX material remap '{remap.SourceMaterialName}' ({remap.TargetMaterial.Guid}) " +
                    $"from '{metadata.SourceMetaPath}' could not be resolved.");
            }

            XRMaterial material = state.Context.GetOrAddCached(
                materialPath,
                () => UnityMaterialImporter.ImportWithReport(materialPath, state.Context).Material
                    ?? throw new UnityVisualImportException($"Unity material importer returned no material for '{materialPath}'."));
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
            state.Context.MarkOutcome(normalized, UnityImportConversionOutcome.Converted);
            return texture;
        });
    }

    private static UnityImportConversionOutcome GetMaterialOutcome(XRMaterial material)
    {
        return MaterialConversionReportRegistry.Instance.TryGet(material, out MaterialConversionReport? report) &&
               report.Outcome == EMaterialConversionOutcome.DowngradedToPoiyomiToon
            ? UnityImportConversionOutcome.Downgraded
            : UnityImportConversionOutcome.Converted;
    }

    private static void IndexModelHierarchy(
        SceneNode node,
        string unityPath,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        long gameObjectFileId = UnityModelFileId.ForGameObject(unityPath);
        long transformFileId = UnityModelFileId.ForTransform(unityPath);
        AddUniqueNodeIdentity(hierarchy.NodesByGameObjectId, gameObjectFileId, node, unityPath, "GameObject", state);
        AddUniqueNodeIdentity(hierarchy.NodesByTransformId, transformFileId, node, unityPath, "Transform", state);
        hierarchy.TransformSortOrders[transformFileId] = node.Parent?.Transform.Children.IndexOf(node.Transform) ?? 0;

        foreach (XRComponent component in node.Components)
        {
            string unityType = component switch
            {
                ModelComponent model when RequiresSkinnedMeshRenderer(model) => "SkinnedMeshRenderer",
                ModelComponent => "MeshRenderer",
                UnityAnimatorImportMetadataComponent => "Animator",
                _ => component.GetType().Name,
            };
            long componentFileId = UnityModelFileId.ForComponent(unityType, unityPath);
            if (!hierarchy.ComponentsByFileId.TryAdd(componentFileId, component))
            {
                state.Context.AddDiagnostic(
                    "UNITYMODEL0003",
                    UnityImportDiagnosticSeverity.Error,
                    UnityImportDiagnosticCategory.ModelIdentity,
                    $"Unity {unityType} fileID collision at '{unityPath}' ({componentFileId}).",
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
            string segment = occurrence == 0 ? name : $"{name}{occurrence}";
            IndexModelHierarchy(child, $"{unityPath}/{segment}", hierarchy, state);
        }
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
            UnityImportDiagnosticSeverity.Error,
            UnityImportDiagnosticCategory.ModelIdentity,
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
            throw new UnityVisualImportException(
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

        string? importedRootName = root.Name;
        importedRoot.Parent = null;
        importedRoot.Name = importedRootName;
        return importedRoot;
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
