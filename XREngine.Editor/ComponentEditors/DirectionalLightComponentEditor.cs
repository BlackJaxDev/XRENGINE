using ImGuiNET;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;

namespace XREngine.Editor.ComponentEditors;

public sealed class DirectionalLightComponentEditor : IXRComponentEditor
{
    /// <summary>
    /// Distinct colors matching the cascade debug colors used in the scene (Lights3DCollection).
    /// </summary>
    private static readonly uint[] CascadeImGuiColors =
    [
        ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.2f, 0.2f, 1.0f)),  // Red
        ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 1.0f, 0.2f, 1.0f)),  // Green
        ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 1.0f, 1.0f)),  // Blue
        ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 1.0f, 0.2f, 1.0f)),  // Yellow
        ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.5f, 0.0f, 1.0f)),  // Orange
        ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.2f, 1.0f, 1.0f)),  // Purple
        ImGui.ColorConvertFloat4ToU32(new Vector4(0.0f, 1.0f, 1.0f, 1.0f)),  // Cyan
        ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.5f, 0.7f, 1.0f)),  // Pink
    ];

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not DirectionalLightComponent light)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
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
        DrawCascadeDebugSection(light);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawDirectionalShadowSection(DirectionalLightComponent light)
    {
        if (!ImGui.CollapsingHeader("Directional Shadow Projection", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.SeparatorText("Orthographic Volume");

        Vector3 scale = light.Scale;
        if (ImGui.DragFloat3("Shadow Volume Scale", ref scale, 0.1f, 0.01f, 100000.0f, "%.3f"))
        {
            scale.X = MathF.Max(0.01f, scale.X);
            scale.Y = MathF.Max(0.01f, scale.Y);
            scale.Z = MathF.Max(0.02f, scale.Z);
            light.Scale = scale;
        }
        ImGuiUndoHelper.TrackDragUndo("Shadow Volume Scale", light);

        ImGui.SeparatorText("Cascade Layout");

        int cascades = light.CascadeCount;
        if (ImGui.SliderInt("Cascade Count", ref cascades, 1, 8))
            light.CascadeCount = cascades;
        ImGuiUndoHelper.TrackDragUndo("Cascade Count", light);

        float overlap = light.CascadeOverlapPercent;
        if (ImGui.SliderFloat("Cascade Overlap %", ref overlap, 0.0f, 1.0f, "%.3f"))
            light.CascadeOverlapPercent = overlap;
        ImGuiUndoHelper.TrackDragUndo("Cascade Overlap", light);

        float cascadeDistance = float.IsFinite(light.CascadedShadowDistance)
            ? light.CascadedShadowDistance
            : 0.0f;
        if (ImGui.DragFloat("Cascade Distance", ref cascadeDistance, 1.0f, 0.0f, 100000.0f, "%.1f"))
            light.CascadedShadowDistance = cascadeDistance;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum camera distance covered by cascade splits.\n0 uses the camera shadow distance / far plane.");
        ImGuiUndoHelper.TrackDragUndo("Cascade Distance", light);

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

    private static void DrawCascadeDebugSection(DirectionalLightComponent light)
    {
        if (!ImGui.CollapsingHeader("Cascade Debug", ImGuiTreeNodeFlags.None))
            return;

        // Debug cascade color overlay toggle
        bool debugColors = light.DebugCascadeColors;
        if (ImGui.Checkbox("Debug Cascade Colors", ref debugColors))
            light.DebugCascadeColors = debugColors;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tint each cascade with a distinct color in the viewport.\nUseful for verifying cascade boundaries.");

        // Preview bounding volume toggle (affects the scene wireframe boxes)
        bool previewVol = light.PreviewBoundingVolume;
        if (ImGui.Checkbox("Preview Cascade Volumes", ref previewVol))
            light.PreviewBoundingVolume = previewVol;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw wireframe OBBs for each cascade slice in the scene.");

        ImGui.Separator();

        // Runtime statistics
        int activeCascades = light.ActiveCascadeCount;
        bool cascadesEnabled = light.EnableCascadedShadows;
        bool castsShadows = light.CastsShadows;

        ImGui.Text($"Casts Shadows: {(castsShadows ? "Yes" : "No")}");
        ImGui.Text($"Cascaded Enabled: {(cascadesEnabled ? "Yes" : "No")}");
        ImGui.Text($"Active Cascades: {activeCascades} / {light.CascadeCount}");
        string configuredDistance = float.IsFinite(light.CascadedShadowDistance)
            ? $"{light.CascadedShadowDistance:F1}"
            : "Auto";
        ImGui.Text($"Configured Cascade Distance: {configuredDistance}");
        ImGui.Text($"Effective Cascade Range: {light.CascadeRangeNear:F1} - {light.CascadeRangeFar:F1} ({light.EffectiveCascadeDistance:F1})");

        var tex = light.CascadedShadowMapTexture;
        if (tex is not null)
            ImGui.Text($"Cascade Texture: {tex.Width}x{tex.Height} x {tex.Depth} layers");
        else
            ImGui.TextDisabled("Cascade texture not allocated.");

        if (activeCascades == 0)
        {
            ImGui.TextDisabled("No active cascades — camera/light frusta may not overlap.");
            return;
        }

        ImGui.Separator();

        // Per-cascade detail table
        if (ImGui.BeginTable("CascadeSlices", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Idx", ImGuiTableColumnFlags.WidthFixed, 30.0f);
            ImGui.TableSetupColumn("Split Near", ImGuiTableColumnFlags.WidthFixed, 80.0f);
            ImGui.TableSetupColumn("Split Far", ImGuiTableColumnFlags.WidthFixed, 80.0f);
            ImGui.TableSetupColumn("Center", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Half Extents", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            for (int i = 0; i < activeCascades; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                // Color indicator matching cascade debug colors
                uint color = CascadeImGuiColors[i % CascadeImGuiColors.Length];
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.Text($"{i}");
                ImGui.PopStyleColor();

                ImGui.TableNextColumn();
                float splitNear = i == 0 ? light.CascadeRangeNear : light.GetCascadeSplit(i - 1);
                ImGui.Text($"{splitNear:F1}");

                ImGui.TableNextColumn();
                ImGui.Text($"{light.GetCascadeSplit(i):F1}");

                ImGui.TableNextColumn();
                Vector3 center = light.GetCascadeCenter(i);
                ImGui.Text($"({center.X:F1}, {center.Y:F1}, {center.Z:F1})");

                ImGui.TableNextColumn();
                Vector3 he = light.GetCascadeHalfExtents(i);
                ImGui.Text($"({he.X:F1}, {he.Y:F1}, {he.Z:F1})");
            }

            ImGui.EndTable();
        }
    }
}
