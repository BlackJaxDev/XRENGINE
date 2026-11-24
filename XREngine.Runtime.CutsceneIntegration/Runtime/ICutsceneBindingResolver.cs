using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Finds and creates the scene nodes that cutscene bindings refer to.
/// A test can supply a fake resolver, so binding code needs no asset input or output.
/// </summary>
public interface ICutsceneBindingResolver
{
    /// <summary>Finds a scene node by its object identifier.</summary>
    /// <param name="id">The identifier of the node.</param>
    /// <returns>The node, or null if no node has the identifier.</returns>
    SceneNode? FindNodeById(Guid id);

    /// <summary>Finds a scene node by its path from a root node.</summary>
    /// <param name="path">The path, with a slash between node names.</param>
    /// <returns>The node, or null if no node has the path.</returns>
    SceneNode? FindNodeByPath(string path);

    /// <summary>Finds a node inside an instance of a prefab.</summary>
    /// <param name="prefabAssetId">The identifier of the prefab asset.</param>
    /// <param name="prefabNodeId">The identifier of the node inside the prefab.</param>
    /// <returns>The node, or null if no instance holds the node.</returns>
    SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId);

    /// <summary>Creates a new instance of a prefab.</summary>
    /// <param name="prefabAssetId">The identifier of the prefab asset.</param>
    /// <returns>The root node of the new instance, or null if the instance could not be created.</returns>
    SceneNode? InstantiatePrefab(Guid prefabAssetId);
}
