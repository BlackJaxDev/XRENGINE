using XREngine.Data.Core;

namespace XREngine.Scene.Prefabs;

public static partial class SceneNodePrefabUtility
{
    /// <summary>Clones ordered detached roots in one construction ledger.</summary>
    internal static ObjectConstructionResult<IReadOnlyList<SceneNode>> CloneHierarchiesOwned(
        IReadOnlyList<SceneNode> templates,
        Action<int, SceneNode, ObjectConstructionContext, Action>? configure = null,
        Action? requireSources = null)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (templates.Count == 0)
            throw new ArgumentException("An owned forest needs at least one source.", nameof(templates));
        SceneNode[] sources = new SceneNode[templates.Count];
        for (int index = 0; index < sources.Length; index++)
            sources[index] = templates[index] ??
                throw new ArgumentException("An owned forest has a null source.", nameof(templates));
        string?[] serialized = new string?[sources.Length];
        for (int index = 0; index < sources.Length; index++)
        {
            requireSources?.Invoke();
            if (!XRRuntimeEnvironment.IsAotRuntimeBuild)
                serialized[index] = AssetManager.Serializer.Serialize(sources[index]);
            requireSources?.Invoke();
        }

        HierarchyConstructionScope? admission = null;
        OwnedHierarchyGraph? forest = null;
        try
        {
            return XRObjectBase.ConstructOwned<IReadOnlyList<SceneNode>>(context =>
            {
                admission = new(context);
                SceneNode[] roots = new SceneNode[sources.Length];
                IReadOnlyList<SceneNode> result = Array.AsReadOnly(roots);
                List<OwnedHierarchyGraph> rootGraphs = [];
                for (int index = 0; index < roots.Length; index++)
                {
                    using (ReserveOtherRoots(rootGraphs, -1))
                    {
                        ValidateGraphs(rootGraphs);
                        roots[index] = serialized[index] is { } text
                            ? AssetManager.Deserializer.Deserialize<SceneNode>(text)
                                ?? throw new InvalidOperationException("Failed to deserialize prefab hierarchy.")
                            : CloneHierarchyBoundedCore(sources[index], context);
                        ValidateGraphs(rootGraphs);
                        requireSources?.Invoke();
                        ValidateGraphs(rootGraphs);
                    }
                    rootGraphs.Add(new(roots[index], context));
                }
                forest = new(roots, context);

                for (int index = 0; index < roots.Length; index++)
                {
                    using (ReserveOtherRoots(rootGraphs, index))
                    {
                        forest.Validate();
                        configure?.Invoke(index, roots[index], context, forest.Validate);
                        forest.Validate();
                        requireSources?.Invoke();
                        forest.Validate();
                    }
                }
                for (int index = 0; index < roots.Length; index++)
                {
                    using (ReserveOtherRoots(rootGraphs, index))
                    {
                        forest.Validate();
                        rootGraphs[index].RegenerateIdentities(forest.Validate);
                        forest.Validate();
                    }
                }
                for (int index = 0; index < roots.Length; index++)
                {
                    using (ReserveOtherRoots(rootGraphs, index))
                    {
                        forest.Validate();
                        rootGraphs[index].Initialize(forest.Validate);
                        forest.Validate();
                    }
                }
                using (ReserveOtherRoots(rootGraphs, -1))
                {
                    forest.Validate();
                    requireSources?.Invoke();
                    forest.Validate();
                }
                return result;
            }, _ =>
            {
                requireSources?.Invoke();
                forest!.Validate();
            });
        }
        finally { admission?.Dispose(); }
    }

    private static HierarchyStructuralReservation ReserveOtherRoots(
        IReadOnlyList<OwnedHierarchyGraph> graphs, int active)
    {
        List<XRWorldObjectBase> peers = [];
        for (int index = 0; index < graphs.Count; index++)
            if (index != active)
                peers.AddRange(graphs[index].StructuralObjects);
        return HierarchyStructuralReservation.TryAcquire(peers) ??
            throw new InvalidOperationException("An owned forest peer has an active structural mutation.");
    }

    private static void ValidateGraphs(IReadOnlyList<OwnedHierarchyGraph> graphs)
    {
        foreach (OwnedHierarchyGraph graph in graphs)
            graph.Validate();
    }
}
