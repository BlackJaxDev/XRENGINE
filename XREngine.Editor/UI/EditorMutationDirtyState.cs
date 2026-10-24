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
        foreach (XRAsset document in ResolveDocuments(target))
            document.MarkDirty();
    }

    /// <summary>Gets the exact source document that owns an editor target.</summary>
    public static XRAsset? ResolveDocument(object target)
    {
        XRAsset[] documents = ResolveDocuments(target);
        return documents.Length == 1 ? documents[0] : null;
    }

    private static XRAsset[] ResolveDocuments(object target)
    {
        if (target is XRAsset asset)
            return [asset.SourceAsset];

        SceneNode? node = target switch
        {
            SceneNode sceneNode => sceneNode,
            XRComponent component => component.SceneNode,
            TransformBase transform => transform.SceneNode,
            _ => null
        };
        if (node?.World is not RuntimeWorld world || world.IsDisposing)
            return [];
        while (node.Parent is { } parent)
            node = parent;
        XRScene[] scenes = XREngine.Editor.HierarchySceneOwnership.CaptureRootScenes(node, world);
        if (scenes.Length > 0)
            return scenes.Select(scene => scene.SourceAsset)
                .Distinct<XRAsset>(ReferenceEqualityComparer.Instance).ToArray();
        return world.TargetWorld is { } document ? [document.SourceAsset] : [];
    }
}
