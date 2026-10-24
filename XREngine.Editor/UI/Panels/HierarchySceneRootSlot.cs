using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Captures the exact scene list and root position owned by one hierarchy edit.</summary>
internal readonly record struct HierarchySceneRootSlot(
    XRScene Scene, List<SceneNode> Roots, int Index, bool Visible, bool Loaded, bool InTargetWorld)
{
    public static HierarchySceneRootSlot Capture(XRScene scene, SceneNode root, RuntimeWorld world)
        => new(scene, scene.RootNodes, scene.RootNodes.IndexOf(root), scene.IsVisible, world.IsSceneLoaded(scene),
            world.TargetWorld?.Scenes.Contains(scene) == true);

    public bool IsCurrent(RuntimeWorld world, EditorWorldIntegration? integration)
        => ReferenceEquals(Scene.RootNodes, Roots) && Scene.IsVisible == Visible &&
            (world.TargetWorld?.Scenes.Contains(Scene) == true) == InTargetWorld &&
            world.IsSceneLoaded(Scene) == Loaded && (!Loaded || world.IsSceneReady(Scene)) &&
            (world.TargetWorld?.Scenes.Contains(Scene) == true ||
             ReferenceEquals(integration?.ExistingEditorScene, Scene) || Loaded);
}
