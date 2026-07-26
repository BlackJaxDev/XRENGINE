using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Components;
using XREngine.Scene.Transforms;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

internal static partial class UnitySceneImporter
{
    private static readonly Regex DocumentHeaderRegex = new(
        @"^---\s*!u!(?<classId>-?\d+)\s*&(?<fileId>-?\d+)(?:\s+stripped)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SceneNode[] Import(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string normalizedPath = Path.GetFullPath(filePath);
        var state = new ImportState(normalizedPath);
        ImportedHierarchy hierarchy = ImportHierarchy(normalizedPath, state);
        return [.. hierarchy.RootEntries.Select(static entry => entry.Node)];
    }

    public static SceneNode ImportPrefab(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string normalizedPath = Path.GetFullPath(filePath);
        var state = new ImportState(normalizedPath);
        ImportedHierarchy hierarchy = ImportHierarchy(normalizedPath, state);
        hierarchy.SortRoots();

        if (hierarchy.RootEntries.Count == 1)
            return hierarchy.RootEntries[0].Node;

        string rootName = Path.GetFileNameWithoutExtension(normalizedPath);
        var rootNode = new SceneNode(rootName, new Transform());
        foreach (ImportedRootEntry rootEntry in hierarchy.RootEntries)
            rootEntry.Node.Parent = rootNode;

        return rootNode;
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
            ParsedUnityFile parsed = ParseUnityFile(filePath);
            var hierarchy = new ImportedHierarchy(filePath);

            foreach (ParsedTransform parsedTransform in parsed.Transforms.Values.OrderBy(static transform => transform.DocumentOrder))
            {
                ParsedGameObject gameObject = parsed.GameObjects.TryGetValue(parsedTransform.GameObjectFileId, out ParsedGameObject? parsedGameObject)
                    ? parsedGameObject
                    : new ParsedGameObject(parsedTransform.GameObjectFileId, $"GameObject {parsedTransform.GameObjectFileId}", true, 0, parsedTransform.DocumentOrder);

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

            foreach (ParsedTransform parsedTransform in parsed.Transforms.Values.OrderBy(static transform => transform.DocumentOrder))
            {
                if (!hierarchy.NodesByTransformId.TryGetValue(parsedTransform.FileId, out SceneNode? parentNode))
                    continue;

                foreach (long childTransformFileId in parsedTransform.ChildTransformFileIds)
                {
                    if (hierarchy.NodesByTransformId.TryGetValue(childTransformFileId, out SceneNode? childNode))
                        childNode.Parent = parentNode;
                }
            }

            foreach (ParsedTransform parsedTransform in parsed.Transforms.Values.OrderBy(static transform => transform.DocumentOrder))
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

            var prefabRootsByInstanceId = new Dictionary<long, List<ImportedRootEntry>>();
            foreach (ParsedPrefabInstance prefabInstance in parsed.PrefabInstances.OrderBy(static instance => instance.DocumentOrder))
            {
                ImportedHierarchy importedPrefab = ImportPrefabInstance(prefabInstance, parsed, hierarchy, state);
                importedPrefab.SortRoots();

                if (prefabInstance.TransformParentFileId != 0 &&
                    hierarchy.NodesByTransformId.TryGetValue(prefabInstance.TransformParentFileId, out SceneNode? parentNode))
                {
                    foreach (ImportedRootEntry rootEntry in importedPrefab.RootEntries)
                        rootEntry.Node.Parent = parentNode;
                }

                prefabRootsByInstanceId[prefabInstance.FileId] = [.. importedPrefab.RootEntries];
            }

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
        ParsedUnityFile ownerFile,
        ImportedHierarchy ownerHierarchy,
        ImportState state)
    {
        string? prefabPath = ResolveAssetPath(state, prefabInstance.SourcePrefab.Guid);
        ImportedHierarchy hierarchy;

        if (!string.IsNullOrWhiteSpace(prefabPath) && File.Exists(prefabPath))
        {
            hierarchy = ImportHierarchy(prefabPath, state);
        }
        else
        {
            string placeholderName = ExtractStringOverride(prefabInstance.Modifications, "m_Name")
                ?? (!string.IsNullOrWhiteSpace(prefabPath)
                    ? Path.GetFileNameWithoutExtension(prefabPath)
                    : $"Missing Prefab {prefabInstance.SourcePrefab.Guid ?? prefabInstance.FileId.ToString(CultureInfo.InvariantCulture)}");

            Debug.LogWarning($"Unity prefab source '{prefabInstance.SourcePrefab.Guid ?? "<missing-guid>"}' could not be resolved while importing '{state.EntryFilePath}'. A placeholder node will be created instead.");
            hierarchy = CreatePlaceholderHierarchy(placeholderName, prefabInstance.DocumentOrder);
        }

        ApplyPrefabModifications(prefabInstance, hierarchy);
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

    private static void ApplyPrefabModifications(ParsedPrefabInstance prefabInstance, ImportedHierarchy hierarchy)
    {
        foreach (IGrouping<long, PropertyModification> group in prefabInstance.Modifications
            .Where(static modification => !string.IsNullOrWhiteSpace(modification.PropertyPath))
            .GroupBy(static modification => modification.TargetFileId))
        {
            if (hierarchy.NodesByGameObjectId.TryGetValue(group.Key, out SceneNode? node))
                ApplyGameObjectModifications(node, group);

            if (hierarchy.NodesByTransformId.TryGetValue(group.Key, out SceneNode? transformNode) &&
                transformNode.Transform is Transform transform)
            {
                ApplyTransformModifications(group.Key, transform, group, hierarchy);
            }

            if (hierarchy.ComponentsByFileId.TryGetValue(group.Key, out XRComponent? component))
                ApplyComponentModifications(component, group);
        }
    }

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
        Vector3 unityPosition = ConvertPosition(transform.Translation);
        Quaternion unityRotation = ConvertRotation(transform.Rotation);
        Vector3 unityScale = transform.Scale;
        int? explicitSortOrder = null;

        foreach (PropertyModification modification in modifications)
        {
            switch (modification.PropertyPath)
            {
                case "m_LocalPosition.x":
                    if (TryParseFloat(modification.Value, out float positionX))
                        unityPosition.X = positionX;
                    break;
                case "m_LocalPosition.y":
                    if (TryParseFloat(modification.Value, out float positionY))
                        unityPosition.Y = positionY;
                    break;
                case "m_LocalPosition.z":
                    if (TryParseFloat(modification.Value, out float positionZ))
                        unityPosition.Z = positionZ;
                    break;
                case "m_LocalRotation.x":
                    if (TryParseFloat(modification.Value, out float rotationX))
                        unityRotation.X = rotationX;
                    break;
                case "m_LocalRotation.y":
                    if (TryParseFloat(modification.Value, out float rotationY))
                        unityRotation.Y = rotationY;
                    break;
                case "m_LocalRotation.z":
                    if (TryParseFloat(modification.Value, out float rotationZ))
                        unityRotation.Z = rotationZ;
                    break;
                case "m_LocalRotation.w":
                    if (TryParseFloat(modification.Value, out float rotationW))
                        unityRotation.W = rotationW;
                    break;
                case "m_LocalScale.x":
                    if (TryParseFloat(modification.Value, out float scaleX))
                        unityScale.X = scaleX;
                    break;
                case "m_LocalScale.y":
                    if (TryParseFloat(modification.Value, out float scaleY))
                        unityScale.Y = scaleY;
                    break;
                case "m_LocalScale.z":
                    if (TryParseFloat(modification.Value, out float scaleZ))
                        unityScale.Z = scaleZ;
                    break;
                case "m_RootOrder":
                    if (TryParseInt(modification.Value, out int rootOrder))
                        explicitSortOrder = rootOrder;
                    break;
            }
        }

        transform.Translation = ConvertPosition(unityPosition);
        transform.Rotation = ConvertRotation(unityRotation);
        transform.Scale = unityScale;

        if (explicitSortOrder.HasValue)
            hierarchy.SetTransformSortOrder(transformFileId, explicitSortOrder.Value);
    }

    private static void ReorderChildren(
        ParsedUnityFile parsed,
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

        foreach (ParsedTransform parsedTransform in parsed.Transforms.Values.OrderBy(static transform => transform.DocumentOrder))
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
        ParsedUnityFile parsed,
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
        foreach (ParsedTransform parsedTransform in parsed.Transforms.Values.OrderBy(static transform => transform.DocumentOrder))
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

    private static ParsedUnityFile ParseUnityFile(string filePath)
    {
        string[] lines = File.ReadAllLines(filePath);
        var gameObjects = new Dictionary<long, ParsedGameObject>();
        var transforms = new Dictionary<long, ParsedTransform>();
        var camerasByGameObjectId = new Dictionary<long, ParsedCamera>();
        var lightsByGameObjectId = new Dictionary<long, ParsedLight>();
        var meshFiltersByGameObjectId = new Dictionary<long, ParsedMeshFilter>();
        var meshRenderersByGameObjectId = new Dictionary<long, ParsedMeshRenderer>();
        var skinnedMeshRenderersByGameObjectId = new Dictionary<long, ParsedSkinnedMeshRenderer>();
        var componentsByFileId = new Dictionary<long, ParsedUnityComponent>();
        var prefabInstances = new List<ParsedPrefabInstance>();
        var sceneRootReferences = new List<long>();

        int? classId = null;
        long fileId = 0;
        int documentOrder = 0;
        var bodyBuilder = new StringBuilder();

        void FlushDocument()
        {
            if (classId is null || bodyBuilder.Length == 0)
                return;

            ProcessDocument(
                classId.Value,
                fileId,
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
                prefabInstances,
                sceneRootReferences);

            bodyBuilder.Clear();
        }

        foreach (string line in lines)
        {
            Match match = DocumentHeaderRegex.Match(line);
            if (match.Success)
            {
                FlushDocument();
                classId = int.Parse(match.Groups["classId"].Value, CultureInfo.InvariantCulture);
                fileId = long.Parse(match.Groups["fileId"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (classId.HasValue)
                bodyBuilder.AppendLine(line);
        }

        FlushDocument();
        var transformIdsByGameObjectId = transforms.Values
            .GroupBy(static transform => transform.GameObjectFileId)
            .ToDictionary(static group => group.Key, static group => group.First().FileId);

        return new ParsedUnityFile(
            gameObjects,
            transforms,
            transformIdsByGameObjectId,
            camerasByGameObjectId,
            lightsByGameObjectId,
            meshFiltersByGameObjectId,
            meshRenderersByGameObjectId,
            skinnedMeshRenderersByGameObjectId,
            componentsByFileId,
            prefabInstances,
            sceneRootReferences);
    }

    private static void ProcessDocument(
        int classId,
        long fileId,
        string body,
        int documentOrder,
        Dictionary<long, ParsedGameObject> gameObjects,
        Dictionary<long, ParsedTransform> transforms,
        Dictionary<long, ParsedCamera> camerasByGameObjectId,
        Dictionary<long, ParsedLight> lightsByGameObjectId,
        Dictionary<long, ParsedMeshFilter> meshFiltersByGameObjectId,
        Dictionary<long, ParsedMeshRenderer> meshRenderersByGameObjectId,
        Dictionary<long, ParsedSkinnedMeshRenderer> skinnedMeshRenderersByGameObjectId,
        Dictionary<long, ParsedUnityComponent> componentsByFileId,
        List<ParsedPrefabInstance> prefabInstances,
        List<long> sceneRootReferences)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(body));

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode rootNode || rootNode.Children.Count == 0)
            return;

        var documentEntry = rootNode.Children.First();
        string documentType = (documentEntry.Key as YamlScalarNode)?.Value ?? string.Empty;
        if (documentEntry.Value is not YamlMappingNode documentMapping)
            return;

        switch (documentType)
        {
            case "GameObject":
                gameObjects[fileId] = ParseGameObject(fileId, documentMapping, documentOrder);
                break;
            case "Transform":
            case "RectTransform":
                transforms[fileId] = ParseTransform(fileId, documentMapping, documentOrder);
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
            case "PrefabInstance":
                prefabInstances.Add(ParsePrefabInstance(fileId, documentMapping, documentOrder));
                break;
            case "SceneRoots":
                sceneRootReferences.AddRange(ParseSceneRoots(documentMapping));
                break;
        }
    }

    private static ParsedGameObject ParseGameObject(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        string name = GetScalarString(mapping, "m_Name") ?? $"GameObject {fileId}";
        bool isActive = (GetScalarInt(mapping, "m_IsActive") ?? 1) != 0;
        int layer = GetScalarInt(mapping, "m_Layer") ?? 0;
        return new ParsedGameObject(fileId, name, isActive, layer, documentOrder);
    }

    private static ParsedTransform ParseTransform(long fileId, YamlMappingNode mapping, int documentOrder)
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
                UnityReference reference = ParseReference(childNode);
                if (reference.FileId != 0)
                    childTransformFileIds.Add(reference.FileId);
            }
        }

        return new ParsedTransform(fileId, gameObjectFileId, parentFileId, childTransformFileIds, position, rotation, scale, rootOrder, documentOrder);
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
        UnityReference meshReference = ParseReference(GetNode(mapping, "m_Mesh"));
        return new ParsedMeshFilter(fileId, gameObjectFileId, meshReference, documentOrder);
    }

    private static ParsedMeshRenderer ParseMeshRenderer(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        long gameObjectFileId = GetReferenceFileId(mapping, "m_GameObject");
        bool enabled = (GetScalarInt(mapping, "m_Enabled") ?? 1) != 0;
        bool castShadows = (GetScalarInt(mapping, "m_CastShadows") ?? 1) != 0;
        bool receiveShadows = (GetScalarInt(mapping, "m_ReceiveShadows") ?? 1) != 0;
        List<UnityReference> materials = ParseReferenceSequence(GetNode(mapping, "m_Materials"));
        return new ParsedMeshRenderer(fileId, gameObjectFileId, enabled, castShadows, receiveShadows, materials, documentOrder);
    }

    private static ParsedSkinnedMeshRenderer ParseSkinnedMeshRenderer(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        long gameObjectFileId = GetReferenceFileId(mapping, "m_GameObject");
        bool enabled = (GetScalarInt(mapping, "m_Enabled") ?? 1) != 0;
        bool castShadows = (GetScalarInt(mapping, "m_CastShadows") ?? 1) != 0;
        bool receiveShadows = (GetScalarInt(mapping, "m_ReceiveShadows") ?? 1) != 0;
        UnityReference meshReference = ParseReference(GetNode(mapping, "m_Mesh"));
        List<UnityReference> materials = ParseReferenceSequence(GetNode(mapping, "m_Materials"));
        List<long> boneTransformFileIds = ParseReferenceSequence(GetNode(mapping, "m_Bones"))
            .Select(static reference => reference.FileId)
            .Where(static fileIdValue => fileIdValue != 0)
            .ToList();
        long rootBoneTransformFileId = ParseReference(GetNode(mapping, "m_RootBone")).FileId;
        return new ParsedSkinnedMeshRenderer(fileId, gameObjectFileId, enabled, castShadows, receiveShadows, materials, meshReference, boneTransformFileIds, rootBoneTransformFileId, documentOrder);
    }

    private static ParsedPrefabInstance ParsePrefabInstance(long fileId, YamlMappingNode mapping, int documentOrder)
    {
        YamlMappingNode? modificationMapping = GetNode(mapping, "m_Modification") as YamlMappingNode;
        long transformParentFileId = modificationMapping is null ? 0 : GetReferenceFileId(modificationMapping, "m_TransformParent");
        var modifications = new List<PropertyModification>();
        List<UnityReference> removedComponents = modificationMapping is null
            ? []
            : ParseReferenceSequence(GetNode(modificationMapping, "m_RemovedComponents"), preferNestedTarget: true);
        List<UnityReference> removedGameObjects = modificationMapping is null
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

                UnityReference target = ParseReference(GetNode(modificationNode, "target"));
                string propertyPath = GetScalarString(modificationNode, "propertyPath") ?? string.Empty;
                string? value = GetScalarString(modificationNode, "value");
                UnityReference objectReference = ParseReference(GetNode(modificationNode, "objectReference"));
                modifications.Add(new PropertyModification(target.FileId, propertyPath, value, objectReference));
            }
        }

        UnityReference sourcePrefab = ParseReference(GetNode(mapping, "m_SourcePrefab"));
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
            UnityReference reference = ParseReference(item);
            if (reference.FileId != 0)
                roots.Add(reference.FileId);
        }

        return roots;
    }

    private static string? ResolveAssetPath(ImportState state, string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;

        if (state.AssetPathsByGuid.TryGetValue(guid, out string? cachedPath))
            return cachedPath;

        EnsureGuidIndex(state);
        return state.AssetPathsByGuid.TryGetValue(guid, out cachedPath) ? cachedPath : null;
    }

    private static void EnsureGuidIndex(ImportState state)
    {
        if (state.AssetIndexInitialized)
            return;

        foreach (string root in EnumerateUnitySearchRoots(state.ProjectRoot))
        {
            foreach (string metaPath in Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories))
            {
                string? guid = TryReadGuid(metaPath);
                if (string.IsNullOrWhiteSpace(guid))
                    continue;

                string assetPath = metaPath[..^5];
                if (File.Exists(assetPath))
                    state.AssetPathsByGuid.TryAdd(guid, assetPath);
            }
        }

        state.AssetIndexInitialized = true;
    }

    private static IEnumerable<string> EnumerateUnitySearchRoots(string projectRoot)
    {
        string assetsRoot = Path.Combine(projectRoot, "Assets");
        if (Directory.Exists(assetsRoot))
            yield return assetsRoot;

        string packagesRoot = Path.Combine(projectRoot, "Packages");
        if (Directory.Exists(packagesRoot))
            yield return packagesRoot;

        if (!Directory.Exists(assetsRoot) && !Directory.Exists(packagesRoot) && Directory.Exists(projectRoot))
            yield return projectRoot;
    }

    private static string? TryReadGuid(string metaPath)
    {
        foreach (string line in File.ReadLines(metaPath))
        {
            const string prefix = "guid: ";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }

        return null;
    }

    private static string ResolveUnityProjectRoot(string sourcePath)
    {
        string normalizedPath = Path.GetFullPath(sourcePath);
        var current = new DirectoryInfo(Path.GetDirectoryName(normalizedPath) ?? normalizedPath);
        while (current is not null)
        {
            if (string.Equals(current.Name, "Assets", StringComparison.OrdinalIgnoreCase))
                return current.Parent?.FullName ?? current.FullName;

            current = current.Parent;
        }

        return Path.GetDirectoryName(normalizedPath) ?? normalizedPath;
    }

    private static string? ExtractStringOverride(IEnumerable<PropertyModification> modifications, string propertyPath)
        => modifications.LastOrDefault(modification => string.Equals(modification.PropertyPath, propertyPath, StringComparison.Ordinal)).Value;

    private static void RegisterParsedComponent<T>(
        T component,
        Dictionary<long, T> byGameObjectId,
        Dictionary<long, ParsedUnityComponent> byFileId)
        where T : ParsedUnityComponent
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

    private static List<UnityReference> ParseReferenceSequence(YamlNode? node, bool preferNestedTarget = false)
    {
        if (node is not YamlSequenceNode sequenceNode)
            return [];

        var references = new List<UnityReference>(sequenceNode.Children.Count);
        foreach (YamlNode child in sequenceNode.Children)
        {
            UnityReference reference = preferNestedTarget
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

            UnityReference target = ParseNestedReference(mapping, "targetCorrespondingSourceObject", "target");
            UnityReference addedObject = ParseNestedReference(mapping, "addedObject", "objectReference", "instance");
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

            UnityReference target = ParseNestedReference(mapping, "targetCorrespondingSourceObject", "target");
            UnityReference addedObject = ParseNestedReference(mapping, "addedObject", "objectReference", "instance");
            int? insertIndex = GetScalarInt(mapping, "insertIndex");
            deltas.Add(new AddedComponentDelta(target, addedObject, insertIndex));
        }

        return deltas;
    }

    private static UnityReference ParseNestedDeltaReference(YamlNode? node)
    {
        if (node is not YamlMappingNode mapping)
            return ParseReference(node);

        UnityReference nested = ParseNestedReference(mapping, "targetCorrespondingSourceObject", "target", "addedObject", "objectReference");
        return nested.FileId != 0 || !string.IsNullOrWhiteSpace(nested.Guid)
            ? nested
            : ParseReference(mapping);
    }

    private static UnityReference ParseNestedReference(YamlMappingNode mapping, params string[] keys)
    {
        foreach (string key in keys)
        {
            UnityReference nested = ParseReference(GetNode(mapping, key));
            if (nested.FileId != 0 || !string.IsNullOrWhiteSpace(nested.Guid))
                return nested;
        }

        return default;
    }

    private static UnityReference ParseReference(YamlNode? node)
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

        return new UnityReference(fileId, guid, type);
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

    private static Vector3 ConvertPosition(Vector3 unityPosition)
        => new(unityPosition.X, unityPosition.Y, -unityPosition.Z);

    private static Quaternion ConvertRotation(Quaternion unityRotation)
        => NormalizeQuaternion(new Quaternion(-unityRotation.X, -unityRotation.Y, unityRotation.Z, unityRotation.W));

    private static Quaternion NormalizeQuaternion(Quaternion quaternion)
        => quaternion.LengthSquared() > 0.000001f ? Quaternion.Normalize(quaternion) : Quaternion.Identity;

    private sealed class ImportState(string entryFilePath)
    {
        public string EntryFilePath { get; } = entryFilePath;
        public string ProjectRoot { get; } = ResolveUnityProjectRoot(entryFilePath);
        public HashSet<string> ActiveImports { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string?> AssetPathsByGuid { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AssetIndexInitialized { get; set; }
    }

    private sealed class ImportedHierarchy(string sourcePath)
    {
        public string SourcePath { get; } = sourcePath;
        public Dictionary<long, SceneNode> NodesByTransformId { get; } = [];
        public Dictionary<long, SceneNode> NodesByGameObjectId { get; } = [];
        public Dictionary<long, XRComponent> ComponentsByFileId { get; } = [];
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

    private sealed record ParsedUnityFile(
        Dictionary<long, ParsedGameObject> GameObjects,
        Dictionary<long, ParsedTransform> Transforms,
        Dictionary<long, long> TransformIdsByGameObjectId,
        Dictionary<long, ParsedCamera> CamerasByGameObjectId,
        Dictionary<long, ParsedLight> LightsByGameObjectId,
        Dictionary<long, ParsedMeshFilter> MeshFiltersByGameObjectId,
        Dictionary<long, ParsedMeshRenderer> MeshRenderersByGameObjectId,
        Dictionary<long, ParsedSkinnedMeshRenderer> SkinnedMeshRenderersByGameObjectId,
        Dictionary<long, ParsedUnityComponent> ComponentsByFileId,
        List<ParsedPrefabInstance> PrefabInstances,
        List<long> SceneRootReferences);

    private sealed record ParsedGameObject(long FileId, string Name, bool IsActive, int Layer, int DocumentOrder);

    private sealed record ParsedTransform(
        long FileId,
        long GameObjectFileId,
        long ParentTransformFileId,
        List<long> ChildTransformFileIds,
        Vector3 LocalPosition,
        Quaternion LocalRotation,
        Vector3 LocalScale,
        int? RootOrder,
        int DocumentOrder);

    private abstract record ParsedUnityComponent(long FileId, long GameObjectFileId, bool Enabled, int DocumentOrder);

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
        : ParsedUnityComponent(FileId, GameObjectFileId, Enabled, DocumentOrder);

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
        : ParsedUnityComponent(FileId, GameObjectFileId, Enabled, DocumentOrder);

    private sealed record ParsedMeshFilter(
        long FileId,
        long GameObjectFileId,
        UnityReference MeshReference,
        int DocumentOrder)
        : ParsedUnityComponent(FileId, GameObjectFileId, true, DocumentOrder);

    private abstract record ParsedRendererComponent(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        bool CastShadows,
        bool ReceiveShadows,
        List<UnityReference> Materials,
        int DocumentOrder)
        : ParsedUnityComponent(FileId, GameObjectFileId, Enabled, DocumentOrder);

    private sealed record ParsedMeshRenderer(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        bool CastShadows,
        bool ReceiveShadows,
        List<UnityReference> Materials,
        int DocumentOrder)
        : ParsedRendererComponent(FileId, GameObjectFileId, Enabled, CastShadows, ReceiveShadows, Materials, DocumentOrder);

    private sealed record ParsedSkinnedMeshRenderer(
        long FileId,
        long GameObjectFileId,
        bool Enabled,
        bool CastShadows,
        bool ReceiveShadows,
        List<UnityReference> Materials,
        UnityReference MeshReference,
        List<long> BoneTransformFileIds,
        long RootBoneTransformFileId,
        int DocumentOrder)
        : ParsedRendererComponent(FileId, GameObjectFileId, Enabled, CastShadows, ReceiveShadows, Materials, DocumentOrder);

    private sealed record ParsedPrefabInstance(
        long FileId,
        UnityReference SourcePrefab,
        long TransformParentFileId,
        List<PropertyModification> Modifications,
        List<UnityReference> RemovedComponents,
        List<UnityReference> RemovedGameObjects,
        List<AddedGameObjectDelta> AddedGameObjects,
        List<AddedComponentDelta> AddedComponents,
        int DocumentOrder);

    private readonly record struct UnityReference(long FileId, string? Guid, int? Type);

    private readonly record struct AddedGameObjectDelta(UnityReference TargetCorrespondingSourceObject, UnityReference AddedObject, int? InsertIndex);

    private readonly record struct AddedComponentDelta(UnityReference TargetCorrespondingSourceObject, UnityReference AddedObject, int? InsertIndex);

    private readonly record struct PropertyModification(long TargetFileId, string PropertyPath, string? Value, UnityReference ObjectReference);
}