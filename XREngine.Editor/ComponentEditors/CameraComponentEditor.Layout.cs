using ImGuiNET;
using XREngine.Components;
using XREngine.Editor.Settings;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Editor.ComponentEditors;

public sealed partial class CameraComponentEditor
{
    /// <summary>
    /// Camera-owned projection/output stay above the pipeline editor. One selected target supplies every settings tab.
    /// </summary>
    internal static void DrawRuntimeCameraEditor(
        XRCamera camera,
        RenderPipeline? defaultPipeline,
        CameraComponent? component,
        HashSet<object> visited,
        XRViewport? viewport = null,
        XRRenderPipelineInstance? pipelineInstance = null)
    {
        RenderPipeline activePipeline = defaultPipeline ?? ResolveCameraEditorPipeline(camera);
        ImGui.PushID(camera.GetHashCode());

        DrawRuntimeCameraProjection(camera, visited);
        DrawCameraPreviewToggle();
        if (Engine.GlobalEditorPreferences.ShowCameraPreviews)
            DrawPreviewSection(camera, component, activePipeline, viewport, pipelineInstance);

        ImGui.Spacing();
        RenderPipeline selectedPipeline = DrawCameraPipelineSelector(camera, activePipeline);
        var schema = selectedPipeline.PostProcessSchema;
        var state = GetEditorPostProcessState(camera, selectedPipeline);
        var context = new PipelineEditorContext(camera, component, activePipeline, selectedPipeline, state, schema);

        if (ImGui.BeginTabBar("CameraEditorTabs"))
        {
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawRuntimeCameraSettings(context);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Post Processing"))
            {
                DrawRuntimeCameraPostProcessing(context);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Debug"))
            {
                DrawRuntimeCameraDebug(context);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawCameraPreviewToggle()
    {
        var preferences = EditorPreferencesService.Current;
        bool show = preferences.GlobalPreferences.ShowCameraPreviews;
        if (ImGui.Checkbox("Show Camera Previews", ref show))
        {
            preferences.GlobalPreferences.ShowCameraPreviews = show;
            preferences.SaveGlobalPreferences();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Global editor preference, saved immediately. Hides camera previews and their enlarged windows without changing camera rendering.");
    }
}
