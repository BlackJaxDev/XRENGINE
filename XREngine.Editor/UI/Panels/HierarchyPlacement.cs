using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Owns a node's exact parent, scene memberships, and runtime routing for structural Undo.</summary>
internal sealed class HierarchyPlacement
{
    private readonly XRWorld? _targetWorld;
    private readonly List<XRScene>? _worldScenes;
    private readonly IRuntimeWorldScenePolicy? _scenePolicy;
    private readonly SceneNode? _anchorRoot;
    private readonly TransformBase? _anchorRootTransform;
    private readonly TransformBase? _anchorParent;
    private readonly HierarchySceneRootSlot[] _anchorScenes;
    private readonly RuntimeHierarchyRootRoutes? _anchorRoutes;
    private readonly bool _anchorInWorldRoots;
    private readonly object? _parentChildren;
    private readonly bool _nodeEditorOnly;
    private readonly bool _anchorRootEditorOnly;
    private readonly IRuntimeWorldContext? _anchorWorld;
    private readonly IRuntimeWorldContext? _parentWorld;
    private readonly ObjectConstructionIdentity[] _dirtySceneIdentities;

    private HierarchyPlacement(SceneNode node, RuntimeWorld world, TransformBase? parent,
        HierarchySceneRootSlot[] scenes, XRScene[] editorRoutes, RuntimeHierarchyRootRoutes runtimeRoutes,
        bool inWorldRoots, EditorWorldIntegration? integration)
    {
        Node = node;
        _nodeEditorOnly = node.IsEditorOnly;
        World = world;
        Transform = node.Transform;
        Parent = parent;
        _parentChildren = parent?.Children;
        _parentWorld = parent?.World;
        Scenes = scenes;
        EditorRoutes = editorRoutes;
        RuntimeRoutes = runtimeRoutes;
        InWorldRoots = inWorldRoots;
        Integration = integration;
        _targetWorld = world.TargetWorld;
        _worldScenes = world.TargetWorld?.Scenes;
        _scenePolicy = world.ScenePolicy;
        if (parent?.SceneNode is { } parentNode)
        {
            _anchorParent = parent.Parent;
            _anchorRoot = HierarchySceneOwnership.GetRoot(parentNode);
            _anchorRootTransform = _anchorRoot?.Transform;
            _anchorRootEditorOnly = _anchorRoot?.IsEditorOnly == true;
            _anchorWorld = _anchorRoot?.World;
            _anchorScenes = _anchorRoot is null ? [] : CaptureSlots(_anchorRoot, world);
            _anchorRoutes = _anchorRoot is null ? null : world.CaptureHierarchyRootRoutes(_anchorRoot);
            _anchorInWorldRoots = _anchorRoot is not null && world.RootNodes.Contains(_anchorRoot);
        }
        else
            _anchorScenes = [];
        HierarchySceneRootSlot[] dirtyScenes = Scenes.Length > 0 ? Scenes : _anchorScenes;
        _dirtySceneIdentities = dirtyScenes.Select(slot => ObjectConstructionIdentity.Capture(slot.Scene)).ToArray();
    }

    public SceneNode Node { get; }
    public RuntimeWorld World { get; }
    public TransformBase Transform { get; }
    public TransformBase? Parent { get; }
    public HierarchySceneRootSlot[] Scenes { get; }
    public XRScene[] EditorRoutes { get; }
    public RuntimeHierarchyRootRoutes RuntimeRoutes { get; }
    public bool InWorldRoots { get; }
    public EditorWorldIntegration? Integration { get; }

    public static HierarchyPlacement Capture(SceneNode node, RuntimeWorld world)
    {
        EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration);
        return new(node, world, node.Transform.Parent, CaptureSlots(node, world),
            integration?.CaptureHierarchySourceRoutes(node) ?? [], world.CaptureHierarchyRootRoutes(node),
            world.RootNodes.Contains(node), integration);
    }

    public static HierarchyPlacement Attached(SceneNode node, RuntimeWorld world, TransformBase parent)
    {
        EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration);
        return new(node, world, parent, [], [], world.PlanHierarchyRootRoutes(node, []), false, integration);
    }

    public static HierarchyPlacement Detached(SceneNode node, RuntimeWorld world)
    {
        EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration);
        return new(node, world, null, [], [], world.PlanHierarchyRootRoutes(node, []), false, integration);
    }

    public static HierarchyPlacement RootLike(SceneNode node, HierarchyPlacement sourceRoot)
    {
        List<HierarchySceneRootSlot> slots = [.. sourceRoot.Scenes.Select(slot => slot with { Index = int.MaxValue })];
        XRScene[] editorRoutes = [.. sourceRoot.EditorRoutes];
        if (sourceRoot.Integration is { } integration)
        {
            if (node.IsEditorOnly && sourceRoot.RuntimeRoutes.Scenes.Count > 0)
            {
                // The first editor-only root allocates the editor scene. An empty editor
                // scene has no runtime effect, so undo keeps it for redo authority checks.
                XRScene editor = integration.EditorScene;
                if (!slots.Any(slot => ReferenceEquals(slot.Scene, editor)))
                    slots.Add(HierarchySceneRootSlot.Capture(editor, node, sourceRoot.World) with { Index = int.MaxValue });
                editorRoutes = [.. sourceRoot.RuntimeRoutes.Scenes];
            }
            else if (!node.IsEditorOnly && sourceRoot.EditorRoutes.Length > 0)
            {
                slots.RemoveAll(slot => ReferenceEquals(slot.Scene, integration.ExistingEditorScene));
                editorRoutes = [];
            }
        }
        bool externallyOwned = sourceRoot.World.CaptureHierarchyRootRoutes(node).ExternallyOwned;
        return new(node, sourceRoot.World, null, [.. slots], editorRoutes,
            sourceRoot.World.PlanHierarchyRootRoutes(node, sourceRoot.RuntimeRoutes.Scenes, externallyOwned),
            sourceRoot.InWorldRoots, sourceRoot.Integration);
    }

    public bool HasAuthority()
    {
        EditorWorldIntegrationRegistry.TryGet(World, out EditorWorldIntegration? integration);
        if (World.IsDisposing || Node.IsDestroyed || Node.IsDestroyQueued ||
            Node.IsEditorOnly != _nodeEditorOnly ||
            !ReferenceEquals(Node.Transform, Transform) || !ReferenceEquals(Transform.SceneNode, Node) ||
            Node.World is not null && !ReferenceEquals(Node.World, World) ||
            !ReferenceEquals(World.TargetWorld, _targetWorld) || !ReferenceEquals(World.TargetWorld?.Scenes, _worldScenes) ||
            !ReferenceEquals(World.ScenePolicy, _scenePolicy) || !ReferenceEquals(integration, Integration))
            return false;
        foreach (HierarchySceneRootSlot slot in Scenes)
            if (!slot.IsCurrent(World, Integration))
                return false;
        foreach (XRScene scene in EditorRoutes)
            if (World.TargetWorld?.Scenes.Contains(scene) != true && !World.IsSceneReady(scene))
                return false;
        if (Parent is null)
            return true;
        if (Parent.IsDestroyed || Parent.IsDestroyQueued || Parent.SceneNode is not { } parentNode ||
            !ReferenceEquals(Parent.Children, _parentChildren) ||
            !ReferenceEquals(parentNode.Transform, Parent) || !ReferenceEquals(Parent.Parent, _anchorParent) ||
            !ReferenceEquals(HierarchySceneOwnership.GetRoot(parentNode), _anchorRoot) ||
            _anchorRoot is null || _anchorRoot.IsDestroyed || _anchorRoot.IsDestroyQueued ||
            _anchorRoot.IsEditorOnly != _anchorRootEditorOnly ||
            !ReferenceEquals(_anchorRoot.Transform, _anchorRootTransform) ||
            !ReferenceEquals(_anchorRoot.World, _anchorWorld) || !ReferenceEquals(Parent.World, _parentWorld) ||
            World.RootNodes.Contains(_anchorRoot) != _anchorInWorldRoots ||
            _anchorRoutes is null || !World.HasHierarchyRootRoutes(_anchorRoutes))
            return false;
        foreach (HierarchySceneRootSlot slot in _anchorScenes)
            if (!slot.IsCurrent(World, Integration) || !slot.Roots.Contains(_anchorRoot))
                return false;
        foreach (XRScene scene in HierarchySceneOwnership.CaptureRootScenes(_anchorRoot, World))
            if (!_anchorScenes.Any(slot => ReferenceEquals(slot.Scene, scene)))
                return false;
        return parentNode.World is null || ReferenceEquals(parentNode.World, World);
    }

    public bool Matches()
    {
        if (!HasAuthority() || !ReferenceEquals(Transform.Parent, Parent) ||
            World.RootNodes.Contains(Node) != InWorldRoots || IsPublishedRoot() != InWorldRoots ||
            !World.HasHierarchyRootRoutes(RuntimeRoutes) ||
            !HasExactScenes() || Integration?.HasHierarchySourceRoutes(Node, EditorRoutes) == false)
            return false;
        IRuntimeWorldContext? expectedWorld = Parent?.World ?? (InWorldRoots ? World : null);
        return ReferenceEquals(Node.World, expectedWorld) &&
            (Parent is null || Parent.Children.Contains(Transform));
    }

    public bool HasExactScenes()
    {
        foreach (HierarchySceneRootSlot slot in Scenes)
            if (!slot.Roots.Contains(Node) || slot.Index != int.MaxValue && slot.Roots.IndexOf(Node) != slot.Index)
                return false;
        foreach (XRScene scene in HierarchySceneOwnership.CaptureRootScenes(Node, World))
            if (!Scenes.Any(slot => ReferenceEquals(slot.Scene, scene)))
                return false;
        return true;
    }

    public void RestoreListsAndRoutes(HierarchyPlacement other, RuntimeHierarchyRootRoutes expectedRoutes,
        IReadOnlyList<XRScene> expectedEditorRoutes, Action? requireBoundary = null)
    {
        requireBoundary?.Invoke();
        if (!World.HasHierarchyRootRoutes(expectedRoutes) ||
            Integration?.HasHierarchySourceRoutes(Node, expectedEditorRoutes) == false)
            throw new InvalidOperationException("The hierarchy route owner changed during the edit.");
        foreach (HierarchySceneRootSlot slot in other.Scenes)
            if (!Scenes.Any(desired => ReferenceEquals(desired.Scene, slot.Scene)))
            {
                requireBoundary?.Invoke();
                slot.Roots.Remove(Node);
                requireBoundary?.Invoke();
            }
        foreach (HierarchySceneRootSlot slot in Scenes)
            if (!slot.Roots.Contains(Node))
            {
                requireBoundary?.Invoke();
                slot.Roots.Insert(Math.Min(slot.Index, slot.Roots.Count), Node);
                requireBoundary?.Invoke();
            }
        requireBoundary?.Invoke();
        World.RestoreHierarchyRootRoutes(expectedRoutes, RuntimeRoutes);
        requireBoundary?.Invoke();
        Integration?.RestoreHierarchySourceRoutes(Node, EditorRoutes);
        requireBoundary?.Invoke();
    }

    public bool IsPublishedRoot()
    {
        foreach (SceneNode root in World.RootNodes.Snapshot)
            if (ReferenceEquals(root, Node))
                return true;
        return false;
    }

    public bool CanRepairWith(HierarchyPlacement other)
    {
        if (!HasAuthority() || !other.HasAuthority() ||
            !ReferenceEquals(Transform.Parent, Parent) && !ReferenceEquals(Transform.Parent, other.Parent))
            return false;
        foreach (XRScene scene in HierarchySceneOwnership.CaptureRootScenes(Node, World))
            if (!Scenes.Any(slot => ReferenceEquals(slot.Scene, scene)) &&
                !other.Scenes.Any(slot => ReferenceEquals(slot.Scene, scene)))
                return false;
        return true;
    }

    public void MarkDirty(Action? requireBoundary = null)
    {
        bool marked = false;
        Exception? failure = null;
        foreach (HierarchySceneRootSlot slot in Scenes.Length > 0 ? Scenes : _anchorScenes)
        {
            try { requireBoundary?.Invoke(); }
            catch (Exception exception)
            {
                failure ??= exception;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            try { slot.Scene.MarkDirty(); }
            catch (Exception exception) { failure ??= exception; }
            marked = true;
            try { requireBoundary?.Invoke(); }
            catch (Exception exception)
            {
                failure ??= exception;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        if (!marked)
        {
            try { requireBoundary?.Invoke(); }
            catch (Exception exception)
            {
                failure ??= exception;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            try { World.TargetWorld?.MarkDirty(); }
            catch (Exception exception) { failure ??= exception; }
            try { requireBoundary?.Invoke(); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void MarkDirtyCapturedDocument(HierarchyCloneDocument document)
    {
        HierarchySceneRootSlot[] targets = Scenes.Length > 0 ? Scenes : _anchorScenes;
        Exception? failure = null;
        for (int i = 0; i < targets.Length; i++)
        {
            HierarchySceneRootSlot slot = targets[i];
            ObjectConstructionIdentity identity = _dirtySceneIdentities[i];
            try
            {
                document.RequireDirtyScene(slot.Scene, slot.Roots, identity);
                slot.Scene.MarkDirty();
                document.RequireDirtyScene(slot.Scene, slot.Roots, identity);
            }
            catch (Exception exception) { failure ??= exception; }
        }
        if (targets.Length == 0)
        {
            try
            {
                document.RequireDirtyWorld();
                World.TargetWorld!.MarkDirty();
                document.RequireDirtyWorld();
            }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static HierarchySceneRootSlot[] CaptureSlots(SceneNode node, RuntimeWorld world)
        => HierarchySceneOwnership.CaptureRootScenes(node, world)
            .Select(scene => HierarchySceneRootSlot.Capture(scene, node, world)).ToArray();
}
