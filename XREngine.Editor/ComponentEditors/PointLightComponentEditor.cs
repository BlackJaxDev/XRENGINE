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
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
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
        DrawPointLightShadowOptions(light);
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
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Also acts as the point-shadow cubemap far plane.");

        float brightness = light.Brightness;
        if (ImGui.DragFloat("Brightness", ref brightness, 0.01f, 0.0f, 100000.0f, "%.3f"))
            light.Brightness = MathF.Max(0.0f, brightness);
    }

    private static void DrawPointLightShadowOptions(PointLightComponent light)
    {
        if (!ImGui.CollapsingHeader("Cubemap Shadow Projection", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        float near = light.ShadowNearPlaneDistance;
        float maxNear = MathF.Max(0.0001f, light.Radius - 0.001f);
        if (ImGui.DragFloat("Near Plane", ref near, 0.001f, 0.0001f, maxNear, "%.4f"))
            light.ShadowNearPlaneDistance = Math.Clamp(near, 0.0001f, maxNear);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keep this as large as possible without clipping nearby shadow casters to improve cubemap precision.");

        bool gs = light.UseGeometryShader;
        if (ImGui.Checkbox("Use Geometry Shader", ref gs))
            light.UseGeometryShader = gs;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Render all 6 cubemap faces in one draw call via geometry shader.\nDisable for 6-pass fallback (useful for debugging or driver compat).");
    }
}
