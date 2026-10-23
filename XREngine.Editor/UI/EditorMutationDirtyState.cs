using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.UI;

/// <summary>Marks the source document that owns an edited object.</summary>
internal static class EditorMutationDirtyState
{
    public static void Mark(object target)
    {
        if (target is XRAsset asset)
        {
            asset.SourceAsset.MarkDirty();
            return;
        }

        SceneNode? node = target switch
        {
            SceneNode sceneNode => sceneNode,
            XRComponent component => component.SceneNode,
            TransformBase transform => transform.SceneNode,
            _ => null
        };
        if (node?.World is not RuntimeWorld world || world.TargetWorld is not { } document)
            return;
        while (node.Parent is { } parent)
            node = parent;
        for (int index = 0; index < document.Scenes.Count; index++)
        {
            XRScene scene = document.Scenes[index];
            if (!scene.RootNodes.Contains(node))
                continue;
            scene.SourceAsset.MarkDirty();
            return;
        }
        document.SourceAsset.MarkDirty();
    }
}
