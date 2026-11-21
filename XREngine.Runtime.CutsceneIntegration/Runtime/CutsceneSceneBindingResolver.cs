using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Scene.Prefabs;

namespace XREngine.Cutscenes;

/// <summary>Resolves cutscene bindings against the root nodes of a live scene.</summary>
public sealed class CutsceneSceneBindingResolver : ICutsceneBindingResolver
{
    private readonly AssetManager _assets;
    private readonly IReadOnlyList<SceneNode> _rootNodes;
    private readonly IRuntimeWorldContext? _world;

    /// <summary>Creates a resolver.</summary>
    /// <param name="assets">The asset manager that provides prefabs.</param>
    /// <param name="rootNodes">The root nodes to search. The list is read at each query.</param>
    /// <param name="world">The world that receives spawned instances, or null for none.</param>
    public CutsceneSceneBindingResolver(AssetManager assets, IReadOnlyList<SceneNode> rootNodes, IRuntimeWorldContext? world = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(rootNodes);
        _assets = assets;
        _rootNodes = rootNodes;
        _world = world;
    }

    /// <summary>
    /// Finds the node with an identifier under the root nodes. The object cache gives one object for each
    /// identifier, and a world copy for play mode has the identifiers of the edit world. The cache keeps the
    /// older object, so the method uses a cached node only if it is under the root nodes, and else searches them.
    /// The search runs only when a cutscene opens.
    /// </summary>
    /// <param name="id">The object identifier.</param>
    /// <returns>The node under the root nodes, or null.</returns>
    public SceneNode? FindNodeById(Guid id)
    {
        if (id == Guid.Empty)
            return null;

        if (XRObjectBase.ObjectsCache.TryGetValue(id, out XRObjectBase? found)
            && found is SceneNode cached
            && IsUnderRootNodes(cached))
            return cached;

        for (int i = 0; i < _rootNodes.Count; i++)
            if (_rootNodes[i] is SceneNode root)
                foreach (SceneNode node in SceneNodePrefabUtility.EnumerateHierarchy(root))
                    if (node.ID == id)
                        return node;

        return null;
    }

    private bool IsUnderRootNodes(SceneNode node)
    {
        SceneNode root = node;
        while (root.Parent is SceneNode parent)
            root = parent;

        for (int i = 0; i < _rootNodes.Count; i++)
            if (ReferenceEquals(_rootNodes[i], root))
                return true;
        return false;
    }

    /// <inheritdoc/>
    public SceneNode? FindNodeByPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        string[] names = path.Split('/');
        for (int i = 0; i < _rootNodes.Count; i++)
            if (_rootNodes[i] is SceneNode root && string.Equals(root.Name, names[0], StringComparison.Ordinal))
                if (Descend(root, names, 1) is SceneNode found)
                    return found;

        return null;
    }

    /// <inheritdoc/>
    public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId)
    {
        if (prefabAssetId == Guid.Empty || prefabNodeId == Guid.Empty)
            return null;

        for (int i = 0; i < _rootNodes.Count; i++)
            if (_rootNodes[i] is SceneNode root)
                foreach (SceneNode node in SceneNodePrefabUtility.EnumerateHierarchy(root))
                    if (node.Prefab?.PrefabAssetId == prefabAssetId
                        && SceneNodePrefabUtility.FindNodeByPrefabId(node, prefabNodeId) is SceneNode found)
                        return found;

        return null;
    }

    /// <inheritdoc/>
    public SceneNode? InstantiatePrefab(Guid prefabAssetId)
        => _assets.InstantiatePrefab(prefabAssetId, _world);

    private static SceneNode? Descend(SceneNode node, string[] names, int index)
    {
        if (index >= names.Length)
            return node;

        foreach (TransformBase child in node.Transform.Children)
            if (child?.SceneNode is SceneNode childNode && string.Equals(childNode.Name, names[index], StringComparison.Ordinal))
                if (Descend(childNode, names, index + 1) is SceneNode found)
                    return found;

        return null;
    }
}
