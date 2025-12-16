using ImGuiNET;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;

namespace XREngine.Editor.ComponentEditors;

public sealed class SpotLightComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not SpotLightComponent light)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(light, visited, "Spot Light Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(light.GetHashCode());

        LightComponentEditorShared.DrawCommonLightSection(light);
        DrawSpotSection(light);
        LightComponentEditorShared.DrawShadowSection(light, showCascadedOptions: false);
        LightComponentEditorShared.DrawShadowMapPreview(light);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawSpotSection(SpotLightComponent light)
    {
        if (!ImGui.CollapsingHeader("Spot", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        float distance = light.Distance;
        if (ImGui.DragFloat("Distance", ref distance, 0.1f, 0.01f, 1000000.0f, "%.3f"))
            light.Distance = MathF.Max(0.01f, distance);

        float brightness = light.Brightness;
        if (ImGui.DragFloat("Brightness", ref brightness, 0.01f, 0.0f, 100000.0f, "%.3f"))
            light.Brightness = MathF.Max(0.0f, brightness);

        float exponent = light.Exponent;
        if (ImGui.DragFloat("Exponent", ref exponent, 0.01f, 0.0f, 1000.0f, "%.3f"))
            light.Exponent = MathF.Max(0.0f, exponent);

        float inner = light.InnerCutoffAngleDegrees;
        float outer = light.OuterCutoffAngleDegrees;

        bool changed = false;
        if (ImGui.SliderFloat("Inner Cutoff (deg)", ref inner, 0.0f, 90.0f, "%.2f"))
            changed = true;
        if (ImGui.SliderFloat("Outer Cutoff (deg)", ref outer, 0.0f, 90.0f, "%.2f"))
            changed = true;

        if (changed)
            light.SetCutoffs(inner, outer);
    }
}
