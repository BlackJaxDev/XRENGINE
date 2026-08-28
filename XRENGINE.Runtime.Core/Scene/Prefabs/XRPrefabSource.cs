using MemoryPack;
using XREngine.Core.Files;

namespace XREngine.Scene.Prefabs;

/// <summary>
/// Serialized runtime-neutral prefab asset that owns a standalone scene-node hierarchy.
/// Source-format import, model reconstruction, and authoring metadata are supplied by
/// feature owners through explicit composition services.
/// </summary>
[Serializable]
[XRAssetInspector("XREngine.Editor.AssetEditors.XRPrefabSourceInspector")]
[MemoryPackable(GenerateType.NoGenerate)]
public partial class XRPrefabSource : XRAsset
{
    private SceneNode? _rootNode;

    /// <summary>
    /// Gets or sets the prefab template hierarchy. Assigned hierarchies receive stable
    /// prefab metadata before they are cloned or serialized.
    /// </summary>
    public SceneNode? RootNode
    {
        get => _rootNode;
        set
        {
            if (SetField(ref _rootNode, value) && value is not null)
                SceneNodePrefabUtility.EnsurePrefabMetadata(value, ID, overwriteExisting: false);
        }
    }

    /// <summary>Creates an independent runtime instance of the prefab hierarchy.</summary>
    public SceneNode Instantiate(
        IRuntimeWorldContext? world = null,
        SceneNode? parent = null,
        bool maintainWorldTransform = false)
    {
        if (RootNode is null)
            throw new InvalidOperationException("Cannot instantiate an empty prefab.");

        SceneNodePrefabUtility.EnsurePrefabMetadata(RootNode, ID, overwriteExisting: false);
        return SceneNodePrefabUtility.Instantiate(
            RootNode,
            ID,
            world,
            parent,
            maintainWorldTransform);
    }
}
