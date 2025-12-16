using ImGuiNET;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;

namespace XREngine.Editor.ComponentEditors;

public sealed class DirectionalLightComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not DirectionalLightComponent light)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(light, visited, "Directional Light Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(light.GetHashCode());

        LightComponentEditorShared.DrawCommonLightSection(light);
        LightComponentEditorShared.DrawShadowSection(light, showCascadedOptions: true);
        LightComponentEditorShared.DrawShadowMapPreview(light);
        DrawDirectionalShadowSection(light);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawDirectionalShadowSection(DirectionalLightComponent light)
    {
        if (!ImGui.CollapsingHeader("Directional Shadows", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        Vector3 scale = light.Scale;
        if (ImGui.DragFloat3("Shadow Volume Scale", ref scale, 0.1f, 0.01f, 100000.0f, "%.3f"))
        {
            scale.X = MathF.Max(0.01f, scale.X);
            scale.Y = MathF.Max(0.01f, scale.Y);
            scale.Z = MathF.Max(0.02f, scale.Z);
            light.Scale = scale;
        }

        int cascades = light.CascadeCount;
        if (ImGui.SliderInt("Cascade Count", ref cascades, 1, 8))
            light.CascadeCount = cascades;

        float overlap = light.CascadeOverlapPercent;
        if (ImGui.SliderFloat("Cascade Overlap %", ref overlap, 0.0f, 1.0f, "%.3f"))
            light.CascadeOverlapPercent = overlap;

        float[] percentages = light.CascadePercentages;
        if (percentages.Length != light.CascadeCount)
            Array.Resize(ref percentages, light.CascadeCount);

        ImGui.SeparatorText("Cascade Percentages");
        ImGui.TextDisabled("Values are normalized to sum to 1.");

        bool anyChanged = false;
        for (int i = 0; i < percentages.Length; i++)
        {
            float v = percentages[i];
            ImGui.PushID(i);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.DragFloat($"Cascade {i}", ref v, 0.001f, 0.0f, 1.0f, "%.4f"))
            {
                percentages[i] = MathF.Max(0.0f, v);
                anyChanged = true;
            }
            ImGui.PopID();
        }

        if (anyChanged)
            light.CascadePercentages = percentages;
    }
}
