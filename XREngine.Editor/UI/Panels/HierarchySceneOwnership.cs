using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Resolves scene membership within the world supplied by the hierarchy owner.</summary>
internal static class HierarchySceneOwnership
{
    public static bool CanAuthor(SceneNode? node, RuntimeWorld world)
    {
        if (world.IsDisposing || node is not { IsDestroyed: false, IsDestroyQueued: false })
            return false;
        if (ReferenceEquals(node.World, world))
            return true;
        return node.World is null && FindOwningScene(node, world) is not null;
    }

    internal static SceneNode? GetRoot(SceneNode node)
    {
        TransformBase? transform = node.Transform;
        while (transform?.Parent is { } parent)
            transform = parent;
        return transform?.SceneNode;
    }

    /// <summary>Copies all root memberships at an authoring boundary, including editor routing.</summary>
    internal static XRScene[] CaptureRootScenes(SceneNode root, RuntimeWorld world)
    {
        List<XRScene> scenes = [];
        if (world.TargetWorld is { } targetWorld)
            foreach (XRScene scene in targetWorld.Scenes)
                if (scene.SceneRoots.Contains(root) && !scenes.Contains(scene))
                    scenes.Add(scene);
        foreach (XRScene scene in world.CaptureHierarchySceneMemberships(root))
            if (!scenes.Contains(scene))
                scenes.Add(scene);
        if (EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration) &&
            integration?.ExistingEditorScene is { } editorScene && editorScene.SceneRoots.Contains(root) &&
            !scenes.Contains(editorScene))
            scenes.Add(editorScene);
        return [.. scenes];
    }

    internal static bool HasRootScenes(SceneNode root, RuntimeWorld world, IReadOnlyList<XRScene> expected)
    {
        foreach (XRScene scene in expected)
            if (!scene.SceneRoots.Contains(root))
                return false;
        foreach (XRScene scene in CaptureRootScenes(root, world))
            if (!expected.Contains(scene))
                return false;
        return true;
    }

    public static XRScene? FindOwningScene(SceneNode? node, RuntimeWorld? world)
    {
        if (node is null || world is null)
            return null;

        if (GetRoot(node) is not { } root)
            return null;
        if (root.World is not null && !ReferenceEquals(root.World, world))
            return null;

        if (world.TargetWorld is { } targetWorld)
            foreach (XRScene scene in targetWorld.Scenes)
                if (scene.SceneRoots.Contains(root))
                    return scene;

        foreach (XRScene scene in world.CaptureHierarchySceneMemberships(root))
            return scene;

        return EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration) &&
            integration?.IsInEditorScene(root) == true ? integration.ExistingEditorScene : null;
    }
}
