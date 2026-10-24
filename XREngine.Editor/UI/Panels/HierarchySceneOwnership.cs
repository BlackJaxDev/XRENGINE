using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Resolves scene membership within the world supplied by the hierarchy owner.</summary>
internal static class HierarchySceneOwnership
{
    public static XRScene? FindOwningScene(SceneNode? node, RuntimeWorld? world)
    {
        if (node is null || world is null)
            return null;

        TransformBase? transform = node.Transform;
        while (transform?.Parent is { } parent)
            transform = parent;
        if (transform?.SceneNode is not { } root)
            return null;

        if (world.TargetWorld is { } targetWorld)
            foreach (XRScene scene in targetWorld.Scenes)
                if (scene.RootNodes.Contains(root))
                    return scene;

        return EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration) &&
            integration?.IsInEditorScene(root) == true ? integration.EditorScene : null;
    }
}
