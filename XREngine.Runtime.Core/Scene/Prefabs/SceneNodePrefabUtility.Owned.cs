using XREngine.Data.Core;

namespace XREngine.Scene.Prefabs;

public static partial class SceneNodePrefabUtility
{
    /// <summary>
    /// Clones a detached hierarchy through the canonical serializer or registered AOT handlers.
    /// The result retains all constructor allocations, including failures and nested assets.
    /// Borrowed assets keep their identities. This receipt does not authorize graph destruction
    /// or prove that a callback did not add a node to an unrelated scene's root list.
    /// </summary>
    public static ObjectConstructionResult<SceneNode> CloneHierarchyOwned(SceneNode template)
    {
        ArgumentNullException.ThrowIfNull(template);
        // Source getters run before the allocation scope; their allocations belong to the source.
        string? serialized = XRRuntimeEnvironment.IsAotRuntimeBuild ? null : AssetManager.Serializer.Serialize(template);
        HierarchyConstructionScope? admission = null;
        OwnedHierarchyGraph? graph = null;
        try
        {
            return XRObjectBase.ConstructOwned(context =>
            {
                admission = new(context);
                SceneNode clone = serialized is null
                    ? CloneHierarchyBoundedCore(template, context)
                    : AssetManager.Deserializer.Deserialize<SceneNode>(serialized)
                        ?? throw new InvalidOperationException("Failed to deserialize prefab hierarchy.");
                graph = new(clone, context);
                graph.RegenerateIdentities();
                graph.Initialize();
                return clone;
            }, _ => graph!.Validate());
        }
        finally { admission?.Dispose(); }
    }
}
