using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Captures the exact scene list and root position owned by one hierarchy edit.</summary>
internal readonly record struct HierarchySceneRootSlot(
    XRScene Scene, SceneRootCollection Roots, int Index, bool Visible, bool Loaded, bool InTargetWorld)
{
    public static HierarchySceneRootSlot Capture(XRScene scene, SceneNode root, RuntimeWorld world)
        => new(scene, scene.SceneRoots, scene.SceneRoots.IndexOf(root), scene.IsVisible, world.IsSceneLoaded(scene),
            world.TargetWorld?.Scenes.Contains(scene) == true);

    public bool IsCurrent(RuntimeWorld world, EditorWorldIntegration? integration)
        => ReferenceEquals(Scene.SceneRoots, Roots) && Scene.IsVisible == Visible &&
            (world.TargetWorld?.Scenes.Contains(Scene) == true) == InTargetWorld &&
            world.IsSceneLoaded(Scene) == Loaded && (!Loaded || world.IsSceneReady(Scene)) &&
            (world.TargetWorld?.Scenes.Contains(Scene) == true ||
             ReferenceEquals(integration?.ExistingEditorScene, Scene) || Loaded);
}
