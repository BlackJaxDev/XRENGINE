using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const float WorkbenchSchemaTreeWidth = 260f;
    private static readonly HashSet<object> _workbenchSettingsVisited = new(ReferenceEqualityComparer.Instance);
    private static InspectorTargetSet? _workbenchSettingsTargets;
    private static RenderPipeline? _workbenchSettingsTargetPipeline;

    /// <summary>
    /// Draws the Settings view: the pipeline asset settings and the post-process schema browser with stage
    /// consumers and the camera values of the selected instance.
    /// </summary>
    private static void DrawWorkbenchSettingsView()
    {
        RenderPipelineWorkbenchModel model = _workbench.Model;
        RenderPipeline? pipeline = model.Pipeline;
        if (pipeline is null)
            return;

        XRCamera? camera = model.Instance?.LastRenderingCamera ?? model.Instance?.LastWindowViewport?.ActiveCamera;
        string? stageKey = _workbench.SettingsShowPipeline ? null : _workbench.Selection.StageKey;
        RenderPipelineWorkbenchSettingsContent content = _workbench.Settings;
        content.Update(model, stageKey, camera);

        ImGui.TextDisabled(content.SchemaSummary);
        if (IsWorkbenchEditing(pipeline))
            DrawWorkbenchStageAuthoring();
        ImGui.BeginChild("##WorkbenchSchemaTree", new Vector2(WorkbenchSchemaTreeWidth, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX);
        DrawWorkbenchSchemaTree(content);
        ImGui.EndChild();
        ImGui.SameLine();

        ImGui.BeginChild("##WorkbenchSettingsDetail", Vector2.Zero, ImGuiChildFlags.Borders);
        if (_workbench.SettingsShowPipeline || stageKey is null)
            DrawWorkbenchPipelineSettings(pipeline, content);
        else
            DrawWorkbenchStageDetail(model, content, camera);
        ImGui.EndChild();
    }

    private static void DrawWorkbenchSchemaTree(RenderPipelineWorkbenchSettingsContent content)
    {
        if (ImGui.Selectable("Pipeline asset settings", _workbench.SettingsShowPipeline))
            _workbench.SettingsShowPipeline = true;
        ImGui.Separator();

        string? selectedStage = _workbench.SettingsShowPipeline ? null : _workbench.Selection.StageKey;
        IReadOnlyList<RenderPipelineSchemaTreeRow> rows = content.Tree;
        for (int i = 0; i < rows.Count; i++)
        {
            RenderPipelineSchemaTreeRow row = rows[i];
            if (row.IsCategory)
            {
                ImGui.Spacing();
                ImGui.TextColored(WorkbenchBadgeColor, row.Label);
                if (row.Tooltip.Length > 0 && ImGui.IsItemHovered())
                    ImGui.SetTooltip(row.Tooltip);
                continue;
            }

            ImGui.Indent(12f);
            ImGui.PushID(i);
            if (row.ConsumerCount == 0)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(WorkbenchWarningColor));
            if (ImGui.Selectable(row.Label, string.Equals(selectedStage, row.StageKey, StringComparison.Ordinal)))
            {
                _workbench.SettingsShowPipeline = false;
                _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForStage(row.StageKey));
            }
            if (row.ConsumerCount == 0)
                ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(row.Tooltip);
            ImGui.PopID();
            ImGui.Unindent(12f);
        }
    }

    private static void DrawWorkbenchPipelineSettings(RenderPipeline pipeline, RenderPipelineWorkbenchSettingsContent content)
    {
        ImGui.TextUnformatted(pipeline.DebugName);
        ImGui.TextColored(WorkbenchWarningColor, "Changes here affect every camera that uses this pipeline asset.");
        ImGui.SeparatorText("Camera-visible settings");
        ImGui.TextDisabled("Camera editors also show these properties for each camera.");
        DrawWorkbenchItems(content.CameraSettingItems);

        ImGui.SeparatorText("All settings");
        if (!ReferenceEquals(_workbenchSettingsTargetPipeline, pipeline))
        {
            _workbenchSettingsTargetPipeline = pipeline;
            _workbenchSettingsTargets = new InspectorTargetSet([pipeline], pipeline.GetType());
        }

        _workbenchSettingsVisited.Clear();
        DrawInspectableObject(_workbenchSettingsTargets!, "WorkbenchPipelineSettings", _workbenchSettingsVisited);
    }

    private static void DrawWorkbenchStageDetail(RenderPipelineWorkbenchModel model, RenderPipelineWorkbenchSettingsContent content, XRCamera? camera)
    {
        if (camera is not null && model.Instance?.LastWindowViewport?.CameraComponent?.SceneNode is { } cameraNode)
        {
            if (ImGui.SmallButton("Open camera in Inspector"))
                Selection.SceneNode = cameraNode;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Selects the camera node. The camera editor edits the values for this camera.");
        }

        DrawWorkbenchItems(content.StageItems);
    }
}
