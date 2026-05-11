using System.Collections.Generic;
using XREngine.Components;
using XREngine.Components.Scene.Environment;
using XREngine.Data.Colors;

namespace XREngine.Editor.ComponentEditors;

public sealed class AtmosphericScatteringComponentEditor : IXRComponentEditor
{
    private static readonly ColorF4 ActiveShellColor = new(0.45f, 0.75f, 1.0f, 0.7f);
    private static readonly ColorF4 InactiveShellColor = new(1.0f, 0.65f, 0.25f, 0.65f);

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not AtmosphericScatteringComponent atmosphere)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        RenderEditingShell(atmosphere);
        EditorImGuiUI.DrawDefaultComponentInspector(atmosphere, visited);
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void RenderEditingShell(AtmosphericScatteringComponent atmosphere)
    {
        if (Engine.Rendering.State.IsShadowPass)
            return;

        float radius = atmosphere.OuterRadius;
        if (!float.IsFinite(radius) || radius <= 0.0f)
            return;

        ColorF4 color = atmosphere.HasRenderableAtmosphere ? ActiveShellColor : InactiveShellColor;
        Engine.Rendering.Debug.RenderSphere(atmosphere.GetPlanetCenter(), radius, solid: false, color);
    }
}
