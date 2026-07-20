namespace XREngine.Scene.Prefabs;

/// <summary>
/// Provides backend-independent prefab ownership and override bookkeeping for scene-node hierarchies.
/// </summary>
public static class SceneNodePrefabMetadataUtility
{
    public static void EnsurePrefabMetadata(SceneNode root, Guid prefabAssetId, bool overwriteExisting = true)
    {
        ArgumentNullException.ThrowIfNull(root);

        foreach (SceneNode node in EnumerateHierarchy(root))
        {
            node.Prefab ??= new SceneNodePrefabLink();
            SceneNodePrefabLink link = node.Prefab;

            if (overwriteExisting || link.PrefabNodeId == Guid.Empty)
                link.PrefabNodeId = Guid.NewGuid();

            if (overwriteExisting || link.PrefabAssetId == Guid.Empty)
                link.PrefabAssetId = prefabAssetId;

            if (ReferenceEquals(node, root))
                link.IsPrefabRoot = true;
            else if (overwriteExisting)
                link.IsPrefabRoot = false;
        }
    }

    public static void ClearPrefabMetadata(SceneNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        foreach (SceneNode node in EnumerateHierarchy(root))
            node.Prefab = null;
    }

    public static void BindInstanceToPrefab(SceneNode instanceRoot, Guid prefabAssetId)
    {
        ArgumentNullException.ThrowIfNull(instanceRoot);

        foreach (SceneNode node in EnumerateHierarchy(instanceRoot))
        {
            node.Prefab ??= new SceneNodePrefabLink();
            node.Prefab.PrefabAssetId = prefabAssetId;

            if (node.Prefab.PrefabNodeId == Guid.Empty)
                node.Prefab.PrefabNodeId = Guid.NewGuid();

            node.Prefab.IsPrefabRoot = ReferenceEquals(node, instanceRoot);
        }
    }

    public static void RecordPropertyOverride(SceneNode node, SceneNodePrefabPropertyOverride overrideData)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(overrideData);
        ArgumentException.ThrowIfNullOrWhiteSpace(overrideData.PropertyPath);

        node.Prefab ??= new SceneNodePrefabLink();
        if (node.Prefab.PrefabNodeId == Guid.Empty)
            node.Prefab.PrefabNodeId = Guid.NewGuid();

        node.Prefab.PropertyOverrides[overrideData.PropertyPath] = CloneOverride(overrideData);
    }

    public static bool RemovePropertyOverride(SceneNode node, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);

        return node.Prefab?.PropertyOverrides is { Count: > 0 } overrides && overrides.Remove(propertyPath);
    }

    public static List<SceneNodePrefabNodeOverride> ExtractOverrides(SceneNode instanceRoot)
    {
        ArgumentNullException.ThrowIfNull(instanceRoot);

        List<SceneNodePrefabNodeOverride> overrides = [];
        foreach (SceneNode node in EnumerateHierarchy(instanceRoot))
            if (BuildNodeOverrideSnapshot(node) is SceneNodePrefabNodeOverride snapshot)
                overrides.Add(snapshot);

        return overrides;
    }

    public static bool HasOverrides(SceneNode node)
        => node.Prefab?.PropertyOverrides.Count > 0;

    public static Dictionary<Guid, SceneNode> BuildPrefabNodeMap(SceneNode instanceRoot)
    {
        ArgumentNullException.ThrowIfNull(instanceRoot);

        Dictionary<Guid, SceneNode> map = [];
        foreach (SceneNode node in EnumerateHierarchy(instanceRoot))
        {
            Guid nodeId = node.Prefab?.PrefabNodeId ?? Guid.Empty;
            if (nodeId != Guid.Empty)
                map[nodeId] = node;
        }

        return map;
    }

    public static SceneNode? FindNodeByPrefabId(SceneNode instanceRoot, Guid prefabNodeId)
    {
        if (prefabNodeId == Guid.Empty)
            return null;

        foreach (SceneNode node in EnumerateHierarchy(instanceRoot))
            if (node.Prefab?.PrefabNodeId == prefabNodeId)
                return node;

        return null;
    }

    public static void ApplyOverrides(
        SceneNode instanceRoot,
        IEnumerable<SceneNodePrefabNodeOverride>? nodeOverrides,
        Func<SceneNode, SceneNodePrefabPropertyOverride, bool> applyPropertyOverride,
        Action<Guid>? missingNode = null)
    {
        ArgumentNullException.ThrowIfNull(instanceRoot);
        ArgumentNullException.ThrowIfNull(applyPropertyOverride);

        if (nodeOverrides is null)
            return;

        Dictionary<Guid, SceneNode> map = BuildPrefabNodeMap(instanceRoot);
        foreach (SceneNodePrefabNodeOverride? overrideEntry in nodeOverrides)
        {
            if (overrideEntry is null || overrideEntry.PrefabNodeId == Guid.Empty)
                continue;

            if (!map.TryGetValue(overrideEntry.PrefabNodeId, out SceneNode? targetNode))
            {
                missingNode?.Invoke(overrideEntry.PrefabNodeId);
                continue;
            }

            foreach (SceneNodePrefabPropertyOverride overrideData in overrideEntry.Properties.Values)
                applyPropertyOverride(targetNode, overrideData);
        }
    }

    public static IEnumerable<SceneNode> EnumerateHierarchy(SceneNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        yield return root;

        foreach (SceneNode child in EnumerateChildren(root))
            foreach (SceneNode descendant in EnumerateHierarchy(child))
                yield return descendant;
    }

    private static IEnumerable<SceneNode> EnumerateChildren(SceneNode node)
    {
        foreach (Transforms.TransformBase? child in node.Transform.Children)
            if (child?.SceneNode is SceneNode childNode)
                yield return childNode;
    }

    private static SceneNodePrefabNodeOverride? BuildNodeOverrideSnapshot(SceneNode node)
    {
        SceneNodePrefabLink? link = node.Prefab;
        if (link is null || link.PrefabNodeId == Guid.Empty || link.PropertyOverrides.Count == 0)
            return null;

        SceneNodePrefabNodeOverride snapshot = new()
        {
            PrefabNodeId = link.PrefabNodeId,
            Properties = new Dictionary<string, SceneNodePrefabPropertyOverride>(link.PropertyOverrides.Count, StringComparer.Ordinal)
        };

        foreach ((string key, SceneNodePrefabPropertyOverride value) in link.PropertyOverrides)
            snapshot.Properties[key] = CloneOverride(value);

        return snapshot;
    }

    private static SceneNodePrefabPropertyOverride CloneOverride(SceneNodePrefabPropertyOverride source)
        => new()
        {
            PropertyPath = source.PropertyPath,
            SerializedValue = source.SerializedValue,
            SerializedType = source.SerializedType
        };
}