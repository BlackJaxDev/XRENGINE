using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Volumes;
using XREngine.Data.Colors;

namespace XREngine.Editor.ComponentEditors;

public sealed class VolumetricFogVolumeComponentEditor : IXRComponentEditor
{
    private static readonly ColorF4 ActiveBoundsColor = new(0.55f, 0.9f, 1.0f, 0.95f);
    private static readonly ColorF4 InactiveBoundsColor = new(1.0f, 0.65f, 0.25f, 0.8f);

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not VolumetricFogVolumeComponent fogVolume)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        RenderEditingBounds(fogVolume);
        EditorImGuiUI.DrawDefaultComponentInspector(fogVolume, visited);
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void RenderEditingBounds(VolumetricFogVolumeComponent fogVolume)
    {
        if (Engine.Rendering.State.IsShadowPass)
            return;

        Matrix4x4 renderMatrix = fogVolume.Transform.RenderMatrix;
        ColorF4 color = fogVolume.HasRenderableVolume ? ActiveBoundsColor : InactiveBoundsColor;
        Engine.Rendering.Debug.RenderBox(fogVolume.HalfExtents, Vector3.Zero, renderMatrix, false, color);
    }
}
