using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Diagnostics;

namespace XREngine.Editor.UI.Tools;

public static class AnimationClipAssetMenuActions
{
    public static void OpenInAnimationClipEditor(XRAssetContextMenuContext context)
    {
        AnimationClip? clip = context.Asset as AnimationClip;
        if (clip is null && Engine.Assets is not null && !string.IsNullOrWhiteSpace(context.AssetPath))
            clip = Engine.Assets.Load(context.AssetPath, typeof(AnimationClip)) as AnimationClip;

        if (clip is null)
        {
            Debug.LogWarning($"Unable to open animation clip editor for '{context.AssetPath}'.");
            return;
        }

        EditorImGuiUI.OpenAnimationClipEditor(clip);
    }
}