using ImGuiNET;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;

namespace XREngine.Editor.ComponentEditors;

public sealed class PointLightComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not PointLightComponent light)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(light, visited, "Point Light Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(light.GetHashCode());

        LightComponentEditorShared.DrawCommonLightSection(light);
        DrawAttenuationSection(light);
        LightComponentEditorShared.DrawShadowSection(light, showCascadedOptions: false);
        LightComponentEditorShared.DrawShadowMapPreview(light);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawAttenuationSection(PointLightComponent light)
    {
        if (!ImGui.CollapsingHeader("Attenuation", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        float radius = light.Radius;
        if (ImGui.DragFloat("Radius", ref radius, 0.1f, 0.01f, 1000000.0f, "%.3f"))
            light.Radius = MathF.Max(0.01f, radius);

        float brightness = light.Brightness;
        if (ImGui.DragFloat("Brightness", ref brightness, 0.01f, 0.0f, 100000.0f, "%.3f"))
            light.Brightness = MathF.Max(0.0f, brightness);
    }
}
