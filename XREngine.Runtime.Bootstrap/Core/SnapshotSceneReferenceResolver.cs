using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine;

/// <summary>
/// Rebinds scene-owned references that cooked snapshot encoding restores as
/// detached objects with the same serialized identity.
/// </summary>
internal static class SnapshotSceneReferenceResolver
{
    public static void Repair(XRScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var nodes = new List<SceneNode>();
        var visitedNodes = new HashSet<SceneNode>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var transformsById = new Dictionary<Guid, TransformBase>();
        var componentsById = new Dictionary<Guid, XRComponent>();
        var physicsChains = new List<PhysicsChainComponent>();

        for (int rootIndex = 0; rootIndex < scene.RootNodes.Count; ++rootIndex)
            CollectCanonicalSceneObjects(
                scene.RootNodes[rootIndex],
                nodes,
                visitedNodes,
                transformsById,
                componentsById);

        int repairedReferenceCount = 0;
        for (int nodeIndex = 0; nodeIndex < nodes.Count; ++nodeIndex)
        {
            SceneNode node = nodes[nodeIndex];
            for (int componentIndex = 0; componentIndex < node.Components.Count; ++componentIndex)
            {
                if (node.Components[componentIndex] is PhysicsChainComponent chain)
                {
                    physicsChains.Add(chain);
                    repairedReferenceCount += RepairPhysicsChain(
                        chain,
                        transformsById,
                        componentsById);
                }
            }
        }

        int rebuiltSkinnedModelCount = RebuildSkinnedModelsWithDetachedBoneReferences(nodes);
        if (repairedReferenceCount > 0 || rebuiltSkinnedModelCount > 0)
        {
            for (int chainIndex = 0; chainIndex < physicsChains.Count; ++chainIndex)
                physicsChains[chainIndex].InvalidateGpuDrivenRenderers();

            SnapshotDiagnostics.Log(
                $"Rebound {repairedReferenceCount} detached scene reference(s) and rebuilt {rebuiltSkinnedModelCount} skinned model(s) after restoring '{scene.Name ?? "<unnamed>"}'.");
        }
    }

    private static void CollectCanonicalSceneObjects(
        SceneNode? node,
        List<SceneNode> nodes,
        HashSet<SceneNode> visitedNodes,
        Dictionary<Guid, TransformBase> transformsById,
        Dictionary<Guid, XRComponent> componentsById)
    {
        if (node is null || !visitedNodes.Add(node))
            return;

        nodes.Add(node);
        AddTransformIdentity(node.Transform, transformsById);

        for (int componentIndex = 0; componentIndex < node.Components.Count; ++componentIndex)
        {
            XRComponent component = node.Components[componentIndex];
            if (component.ID != Guid.Empty)
                componentsById[component.ID] = component;
        }

        for (int childIndex = 0; childIndex < node.Transform.Children.Count; ++childIndex)
        {
            SceneNode? child = node.Transform.Children[childIndex]?.SceneNode;
            CollectCanonicalSceneObjects(
                child,
                nodes,
                visitedNodes,
                transformsById,
                componentsById);
        }
    }

    private static void AddTransformIdentity(
        TransformBase transform,
        Dictionary<Guid, TransformBase> transformsById)
    {
        if (transform.ID != Guid.Empty)
            transformsById[transform.ID] = transform;

        Guid referenceId = transform.EffectiveSerializedReferenceId;
        if (referenceId != Guid.Empty)
            transformsById[referenceId] = transform;
    }

    private static int RepairPhysicsChain(
        PhysicsChainComponent chain,
        IReadOnlyDictionary<Guid, TransformBase> transformsById,
        IReadOnlyDictionary<Guid, XRComponent> componentsById)
    {
        int repairedCount = 0;

        repairedCount += RebindTransform(chain.Root, transformsById, out Transform? root);
        chain.Root = root;

        repairedCount += RebindTransforms(chain.Roots, transformsById, out List<Transform>? roots);
        chain.Roots = roots;

        repairedCount += RebindTransforms(chain.Exclusions, transformsById, out List<TransformBase>? exclusions);
        chain.Exclusions = exclusions;

        repairedCount += RebindTransform(chain.ReferenceObject, transformsById, out Transform? referenceObject);
        chain.ReferenceObject = referenceObject;

        repairedCount += RebindTransform(chain.RootBone, transformsById, out TransformBase? rootBone);
        chain.RootBone = rootBone;

        repairedCount += RebindColliders(chain.Colliders, componentsById, out List<PhysicsChainColliderBase>? colliders);
        chain.Colliders = colliders;

        return repairedCount;
    }

    private static int RebuildSkinnedModelsWithDetachedBoneReferences(
        IReadOnlyList<SceneNode> nodes)
    {
        int rebuiltCount = 0;
        for (int nodeIndex = 0; nodeIndex < nodes.Count; ++nodeIndex)
        {
            SceneNode node = nodes[nodeIndex];
            ModelComponent? modelComponent = node.GetComponent<ModelComponent>();
            if (modelComponent?.Model is null
                || !NeedsRuntimeTransformRebind(modelComponent))
                continue;

            modelComponent.RebuildRuntimeMeshes();
            ++rebuiltCount;
        }

        return rebuiltCount;
    }

    private static bool NeedsRuntimeTransformRebind(ModelComponent modelComponent)
    {
        SceneNode rootNode = modelComponent.SceneNode;
        while (rootNode.Parent is SceneNode parent)
            rootNode = parent;

        TransformBase searchRoot = rootNode.Transform;
        foreach (Rendering.Models.SubMesh subMesh in modelComponent.Model!.Meshes)
        {
            foreach (Rendering.Models.SubMeshLOD lod in subMesh.LODs)
            {
                XRMesh? mesh = lod.Mesh;
                if (mesh is not null && mesh.NeedsSerializedTransformRebind(searchRoot))
                    return true;
            }
        }

        return false;
    }

    private static int RebindTransform<TTransform>(
        TTransform? source,
        IReadOnlyDictionary<Guid, TransformBase> transformsById,
        out TTransform? resolved)
        where TTransform : TransformBase
    {
        resolved = source;
        if (source is null)
            return 0;

        Guid referenceId = source.EffectiveSerializedReferenceId;
        if (referenceId == Guid.Empty
            || !transformsById.TryGetValue(referenceId, out TransformBase? canonical)
            || canonical is not TTransform typedCanonical
            || ReferenceEquals(source, typedCanonical))
            return 0;

        resolved = typedCanonical;
        return 1;
    }

    private static int RebindTransforms<TTransform>(
        List<TTransform>? sources,
        IReadOnlyDictionary<Guid, TransformBase> transformsById,
        out List<TTransform>? resolved)
        where TTransform : TransformBase
    {
        resolved = sources;
        if (sources is null || sources.Count == 0)
            return 0;

        List<TTransform>? replacements = null;
        int repairedCount = 0;
        for (int index = 0; index < sources.Count; ++index)
        {
            TTransform source = sources[index];
            repairedCount += RebindTransform(source, transformsById, out TTransform? canonical);
            if (!ReferenceEquals(source, canonical))
            {
                replacements ??= [.. sources];
                replacements[index] = canonical!;
            }
        }

        resolved = replacements ?? sources;
        return repairedCount;
    }

    private static int RebindColliders(
        List<PhysicsChainColliderBase>? sources,
        IReadOnlyDictionary<Guid, XRComponent> componentsById,
        out List<PhysicsChainColliderBase>? resolved)
    {
        resolved = sources;
        if (sources is null || sources.Count == 0)
            return 0;

        List<PhysicsChainColliderBase>? replacements = null;
        int repairedCount = 0;
        for (int index = 0; index < sources.Count; ++index)
        {
            PhysicsChainColliderBase source = sources[index];
            if (source.ID == Guid.Empty
                || !componentsById.TryGetValue(source.ID, out XRComponent? component)
                || component is not PhysicsChainColliderBase canonical
                || ReferenceEquals(source, canonical))
                continue;

            replacements ??= [.. sources];
            replacements[index] = canonical;
            ++repairedCount;
        }

        resolved = replacements ?? sources;
        return repairedCount;
    }
}
