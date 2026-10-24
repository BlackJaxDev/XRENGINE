using XREngine.Scene;

namespace XREngine;

public sealed partial class RuntimeWorld
{
    /// <summary>Copies loaded scene memberships for one node at an authoring boundary.</summary>
    public XRScene[] CaptureHierarchySceneMemberships(SceneNode node)
    {
        RequireHierarchyRouteOwner(node);
        List<XRScene> scenes = [];
        foreach (XRScene scene in _loadedScenes)
            if (scene.RootNodes.Contains(node))
                scenes.Add(scene);
        return [.. scenes];
    }

    /// <summary>Copies one root's routing metadata at an authoring boundary.</summary>
    public RuntimeHierarchyRootRoutes CaptureHierarchyRootRoutes(SceneNode root)
    {
        RequireHierarchyRouteOwner(root);
        List<XRScene> scenes = [];
        foreach (var pair in _visibleRootsByScene)
            if (pair.Value.Contains(root))
            {
                if (!IsSceneReady(pair.Key))
                    throw new InvalidOperationException("A scene route is still attaching or awaiting cleanup.");
                scenes.Add(pair.Key);
            }
        return new RuntimeHierarchyRootRoutes(this, root, [.. scenes], _externallyOwnedSceneRoots.Contains(root));
    }

    /// <summary>Creates an explicit destination receipt without changing scene or root state.</summary>
    public RuntimeHierarchyRootRoutes PlanHierarchyRootRoutes(SceneNode root,
        IReadOnlyList<XRScene> loadedSceneRoutes, bool externallyOwned = false)
    {
        RequireHierarchyRouteOwner(root);
        ArgumentNullException.ThrowIfNull(loadedSceneRoutes);
        List<XRScene> scenes = [];
        foreach (XRScene scene in loadedSceneRoutes)
        {
            if (!IsSceneReady(scene) || !scene.IsVisible)
                throw new InvalidOperationException("A planned route requires an attached visible scene.");
            if (!scenes.Contains(scene))
                scenes.Add(scene);
        }
        return new RuntimeHierarchyRootRoutes(this, root, [.. scenes], externallyOwned);
    }

    /// <summary>Restores a receipt only while its exact current route owner still matches.</summary>
    public void RestoreHierarchyRootRoutes(RuntimeHierarchyRootRoutes expected, RuntimeHierarchyRootRoutes desired)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(desired);
        RequireHierarchyRouteReceipt(expected);
        RequireHierarchyRouteReceipt(desired);
        if (!ReferenceEquals(expected.Root, desired.Root) || !HasHierarchyRootRoutes(expected))
            throw new InvalidOperationException("The hierarchy root's routing ownership changed before restoration.");
        foreach (XRScene scene in desired.Scenes)
            if (!IsSceneReady(scene) || !scene.IsVisible || !scene.RootNodes.Contains(desired.Root) ||
                desired.Root.Transform.Parent is not null)
                throw new InvalidOperationException("The restored scene route no longer owns a visible root.");
        ApplyHierarchyRouteMetadata(desired);
    }

    /// <summary>Checks a receipt without changing root lifetime or scene visibility.</summary>
    public bool HasHierarchyRootRoutes(RuntimeHierarchyRootRoutes receipt)
    {
        if (!IsHierarchyRouteReceiptCurrent(receipt))
            return false;
        foreach (XRScene scene in receipt.Scenes)
            if (!IsSceneReady(scene) || !scene.IsVisible)
                return false;
        int count = 0;
        foreach (var pair in _visibleRootsByScene)
            if (pair.Value.Contains(receipt.Root))
            {
                if (!receipt.Scenes.Contains(pair.Key))
                    return false;
                count++;
            }
        return count == receipt.Scenes.Count &&
            _externallyOwnedSceneRoots.Contains(receipt.Root) == receipt.ExternallyOwned;
    }

    private void RequireHierarchyRouteOwner(SceneNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ThrowIfDisposed();
        if (IsDisposing || root.IsDestroyed || root.IsDestroyQueued ||
            root.World is not null && !ReferenceEquals(root.World, this))
            throw new InvalidOperationException("The hierarchy root no longer belongs to this live world.");
    }

    private bool IsHierarchyRouteReceiptCurrent(RuntimeHierarchyRootRoutes receipt)
        => ReferenceEquals(receipt.Owner, this) && !IsDisposing &&
            !receipt.Root.IsDestroyed && !receipt.Root.IsDestroyQueued &&
            ReferenceEquals(receipt.Root.Transform, receipt.Transform) &&
            (receipt.Root.World is null || ReferenceEquals(receipt.Root.World, this)) &&
            ReferenceEquals(TargetWorld, receipt.TargetWorld) && ReferenceEquals(ScenePolicy, receipt.ScenePolicy);

    private void RequireHierarchyRouteReceipt(RuntimeHierarchyRootRoutes receipt)
    {
        if (!IsHierarchyRouteReceiptCurrent(receipt))
            throw new InvalidOperationException("The hierarchy route receipt's world, transform, or policy changed.");
    }

    private void ApplyHierarchyRouteMetadata(RuntimeHierarchyRootRoutes desired)
    {
        // This metadata change has no callbacks. Root lifetime remains with RootNodes.
        foreach (HashSet<SceneNode> roots in _visibleRootsByScene.Values)
            roots.Remove(desired.Root);
        foreach (XRScene scene in desired.Scenes)
        {
            if (!_visibleRootsByScene.TryGetValue(scene, out HashSet<SceneNode>? roots))
                _visibleRootsByScene.Add(scene, roots = new(ReferenceEqualityComparer.Instance));
            roots.Add(desired.Root);
        }
        if (desired.ExternallyOwned)
            _externallyOwnedSceneRoots.Add(desired.Root);
        else
            _externallyOwnedSceneRoots.Remove(desired.Root);
    }
}
