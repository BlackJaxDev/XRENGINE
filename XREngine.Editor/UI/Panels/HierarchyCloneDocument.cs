using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Proves current document custody without replaying a clone's old placement.</summary>
internal sealed class HierarchyCloneDocument
{
    private readonly XRWorld? _asset;
    private readonly List<XRScene>? _scenes;
    private readonly IRuntimeWorldScenePolicy? _policy;
    private readonly EditorWorldIntegration? _integration;

    internal HierarchyCloneDocument(RuntimeWorld world)
    {
        World = world;
        _asset = world.TargetWorld;
        _scenes = _asset?.Scenes;
        _policy = world.ScenePolicy;
        EditorWorldIntegrationRegistry.TryGet(world, out _integration);
        if (!IsCurrent())
            throw new InvalidOperationException("The clone requires a live authoring document.");
    }

    internal RuntimeWorld World { get; }

    internal bool IsCurrent()
    {
        EditorWorldIntegrationRegistry.TryGet(World, out EditorWorldIntegration? integration);
        return !World.IsDisposing && _asset?.IsDestroyed != true && _asset?.IsDestroyQueued != true &&
            ReferenceEquals(World.TargetWorld, _asset) && ReferenceEquals(_asset?.Scenes, _scenes) &&
            ReferenceEquals(World.ScenePolicy, _policy) && ReferenceEquals(integration, _integration);
    }

    internal void RequireCustody(ObjectConstructionOwnership ownership)
    {
        if (!IsCurrent())
            throw new InvalidOperationException("The clone's document identity changed before history release.");
        // This cold release check uses exact Core fields. No virtual getter or
        // application callback runs while the structural gate is held.
        lock (SceneRootCollection.MutationGate)
        {
            HashSet<XRWorldObjectBase> endpoints = new(ReferenceEqualityComparer.Instance);
            foreach (XRObjectBase allocation in ownership.Objects)
                if (allocation is XRWorldObjectBase structural)
                    CaptureEndpoints(structural, endpoints);
            using HierarchyStructuralReservation reservation =
                HierarchyStructuralReservation.TryAcquire([.. endpoints]) ??
                throw new InvalidOperationException("The clone's current document links are still being changed.");
            foreach (XRObjectBase allocation in ownership.Objects)
            {
                if (allocation is not XRWorldObjectBase structural)
                    continue;
                RequireLive(structural);
                TransformBase transform = GetTransform(structural);
                if (structural is TransformBase orphan && IsRetainedPrivateTransform(orphan, ownership))
                    continue;
                if (structural is SceneNode node && !ReferenceEquals(transform.OwnedRetirementNode, node))
                    throw new InvalidOperationException("An allocated node no longer owns its exact transform.");
                if (structural is XRComponent component)
                    RequireComponent(component);
                RequireDocumentPath(transform);
            }
        }
        if (!IsCurrent())
            throw new InvalidOperationException("The clone's document changed during custody transfer.");
    }

    private static void CaptureEndpoints(XRWorldObjectBase value, HashSet<XRWorldObjectBase> endpoints)
    {
        endpoints.Add(value);
        TransformBase transform = GetTransform(value);
        HashSet<TransformBase> chain = new(ReferenceEqualityComparer.Instance);
        for (TransformBase? current = transform; current is not null; current = current.OwnedRetirementParent)
        {
            if (!chain.Add(current))
                throw new InvalidOperationException("The retained clone has a cyclic parent chain.");
            endpoints.Add(current);
            if (current.OwnedRetirementNode is { } node)
                endpoints.Add(node);
        }
        if (value is XRComponent component && component.OwnedRetirementNode is { } owner)
            endpoints.Add(owner);
    }

    private static TransformBase GetTransform(XRWorldObjectBase value)
        => value switch
        {
            TransformBase transform => transform,
            SceneNode node when node.OwnedRetirementTransform is { } transform => transform,
            XRComponent component when component.OwnedRetirementNode?.OwnedRetirementTransform is { } transform => transform,
            _ => throw new InvalidOperationException("An allocated structural object has no current document link.")
        };

    private void RequireDocumentPath(TransformBase transform)
    {
        for (TransformBase current = transform; ; current = current.OwnedRetirementParent!)
        {
            RequireLive(current);
            if (current.World is not null && !ReferenceEquals(current.World, World))
                throw new InvalidOperationException("A clone transform belongs to another runtime world.");
            SceneNode? node = current.OwnedRetirementNode;
            if (node is not null)
            {
                RequireLive(node);
                if (!ReferenceEquals(node.OwnedRetirementTransform, current) ||
                    !ReferenceEquals(node.World, current.World) || node.CaptureHierarchyListMemberships().Length != 0)
                    throw new InvalidOperationException("The clone's node and transform no longer have one exact owner.");
                RequireRootLinks(node);
            }
            TransformBase? parent = current.OwnedRetirementParent;
            HierarchyListMembership[] links = current.CaptureHierarchyListMemberships();
            if (parent is null)
            {
                if (links.Length != 0 || node is null || !HasDocumentRoot(node))
                    throw new InvalidOperationException("The clone no longer reaches a root owned by its document.");
                return;
            }
            if (!ReferenceEquals(current.World, parent.World) || links.Length != 1 ||
                !links[0].IsCurrent || links[0].Count != 1 || !ReferenceEquals(links[0].Owner, parent) ||
                !ReferenceEquals(links[0].Collection, parent.OwnedRetirementChildren))
                throw new InvalidOperationException("A clone child has an incomplete or foreign structural list owner.");
        }
    }

    private static void RequireComponent(XRComponent component)
    {
        SceneNode? owner = component.OwnedRetirementNode;
        HierarchyListMembership[] links = component.CaptureHierarchyListMemberships();
        if (owner is null || !ReferenceEquals(component.World, owner.World) || links.Length != 1 ||
            !links[0].IsCurrent || links[0].Count != 1 || !ReferenceEquals(links[0].Owner, owner) ||
            !ReferenceEquals(links[0].Collection, owner.OwnedRetirementComponents))
            throw new InvalidOperationException("A clone component has an incomplete or foreign owner.");
    }

    private static bool IsRetainedPrivateTransform(TransformBase transform, ObjectConstructionOwnership ownership)
    {
        // YAML can replace the default constructor's transform. Keep that private
        // allocation in custody; it has no document edge and gains no cleanup credit.
        EventList<TransformBase>? children = transform.OwnedRetirementChildren;
        return transform.OwnedRetirementNode is null && transform.OwnedRetirementParent is null &&
            transform.World is null && transform.CaptureHierarchyListMemberships().Length == 0 &&
            children is { IsDestroyed: false, IsDestroyQueued: false, Count: 0 } && ownership.Contains(children);
    }

    private void RequireRootLinks(SceneNode node)
    {
        foreach (RootNodeCollection runtimeRoots in node.CaptureRuntimeRootMemberships())
            if (!ReferenceEquals(runtimeRoots, World.RootNodes))
                throw new InvalidOperationException("A clone node has a root link in another runtime world.");
        foreach (SceneRootMembership membership in node.CaptureSceneRootMemberships())
            if (membership.Count < 1 || !membership.Collection.IsCurrent || !OwnsScene(membership.Scene))
                throw new InvalidOperationException("A clone node has a root link in another scene document.");
    }

    private bool HasDocumentRoot(SceneNode node)
    {
        RootNodeCollection[] runtimeRoots = node.CaptureRuntimeRootMemberships();
        if (runtimeRoots.Length == 1 && ReferenceEquals(runtimeRoots[0], World.RootNodes) && ReferenceEquals(node.World, World))
        {
            foreach (SceneNode published in World.RootNodes.Snapshot)
                if (ReferenceEquals(published, node))
                    return true;
        }
        foreach (SceneRootMembership membership in node.CaptureSceneRootMemberships())
            if (membership.Collection.IsCurrent && OwnsScene(membership.Scene))
                return true;
        return false;
    }

    private bool OwnsScene(XRScene scene)
    {
        if (scene.IsDestroyed || scene.IsDestroyQueued)
            return false;
        if (ReferenceEquals(scene, _integration?.ExistingEditorScene))
            return true;
        if (World.IsSceneReady(scene))
            return true;
        if (_scenes is not null)
            foreach (XRScene candidate in _scenes)
                if (ReferenceEquals(candidate, scene))
                    return true;
        return false;
    }

    private static void RequireLive(XRWorldObjectBase value)
    {
        if (value.IsDestroyed || value.IsDestroyQueued)
            throw new InvalidOperationException("A clone allocation has an incomplete or ended lifetime.");
    }
}
