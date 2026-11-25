using XREngine.Data.Core;

namespace XREngine.Scene.Prefabs;

public static partial class SceneNodePrefabUtility
{
    /// <summary>
    /// Clones a detached hierarchy through the canonical serializer or registered AOT handlers.
    /// The result retains all constructor allocations, including failures and nested assets.
    /// Borrowed assets keep their identities. Scene root admission remains blocked until transfer.
    /// This receipt does not authorize graph destruction after transfer.
    /// </summary>
    public static ObjectConstructionResult<SceneNode> CloneHierarchyOwned(SceneNode template,
        Action<SceneNode, ObjectConstructionContext, Action>? configure = null,
        Action? requireSource = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        // Source getters run before the allocation scope; their allocations belong to the source.
        requireSource?.Invoke();
        string? serialized = XRRuntimeEnvironment.IsAotRuntimeBuild ? null : AssetManager.Serializer.Serialize(template);
        requireSource?.Invoke();
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
                configure?.Invoke(clone, context, graph.Validate);
                graph.Validate();
                requireSource?.Invoke();
                graph.Validate();
                graph.RegenerateIdentities();
                graph.Initialize();
                return clone;
            }, _ =>
            {
                requireSource?.Invoke();
                graph!.Validate();
            });
        }
        finally { admission?.Dispose(); }
    }
}
