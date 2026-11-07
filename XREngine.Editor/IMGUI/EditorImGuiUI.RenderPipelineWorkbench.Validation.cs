using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Validation;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly Vector4 WorkbenchInfoColor = new(0.6f, 0.6f, 0.65f, 1f);

    /// <summary>Runs the validator for the Workbench target when an input changed.</summary>
    private static void UpdateWorkbenchValidation()
    {
        RenderPipelineWorkbenchAuthoring authoring = _workbench.Authoring;
        if (authoring.Draft is { } draft && authoring.IsEditing(_workbench.Model.Pipeline))
        {
            // The views show the draft, so the counter and the Validation view show the draft result. An older result
            // stays visible until the result of the current revision arrives.
            RenderPipelineDraftValidation.Result? latest = authoring.Validation.Latest;
            _workbench.Validation.UpdateDraft(latest is not null && ReferenceEquals(latest.Draft, draft) ? latest : null, _workbench.Model);
            return;
        }

        _workbench.Validation.Update(_workbench.Model, AbstractRenderer.Current?.BackendId ?? RendererBackendId.OpenGL);
    }

    /// <summary>Draws the problem counter of the target bar. A click opens the Validation view.</summary>
    private static void DrawWorkbenchTargetBarExtras(RenderPipeline pipeline, XRRenderPipelineInstance? instance)
    {
        UpdateWorkbenchValidation();
        RenderPipelineWorkbenchValidation validation = _workbench.Validation;
        if (validation.CounterText.Length == 0)
            return;

        ImGui.SameLine();
        ImGui.TextColored(validation.Report.ErrorCount > 0 ? WorkbenchErrorColor : WorkbenchWarningColor, validation.CounterText);
        if (ImGui.IsItemClicked())
            _workbench.RequestedView = ERenderPipelineWorkbenchView.Validation;
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(validation.SummaryText);
        ImGui.TextDisabled("Click to open the Validation view.");
        ImGui.EndTooltip();
    }

    /// <summary>
    /// Draws the Validation view: the problems of the validator rules and the pipeline sources, sorted by severity.
    /// "Go to" selects the object of the problem and opens the view that shows it.
    /// </summary>
    private static void DrawWorkbenchValidationView()
    {
        UpdateWorkbenchValidation();
        RenderPipelineWorkbenchValidation validation = _workbench.Validation;

        ImGui.TextUnformatted(validation.SummaryText);
        ImGui.SameLine();
        bool showErrors = validation.ShowErrors;
        if (ImGui.Checkbox("Errors", ref showErrors))
            validation.ShowErrors = showErrors;
        ImGui.SameLine();
        bool showWarnings = validation.ShowWarnings;
        if (ImGui.Checkbox("Warnings", ref showWarnings))
            validation.ShowWarnings = showWarnings;
        ImGui.SameLine();
        bool showInfo = validation.ShowInfo;
        if (ImGui.Checkbox("Info", ref showInfo))
            validation.ShowInfo = showInfo;
        ImGui.SameLine();
        if (ImGui.SmallButton("Run again"))
            validation.Invalidate();

        if (validation.VisibleRows.Count == 0)
        {
            ImGui.TextDisabled(validation.Report.Problems.Count == 0 ? "The validator found no problems." : "The filter hides every problem.");
            return;
        }

        DrawWorkbenchValidationRows(validation.VisibleRows);
    }

    private static unsafe void DrawWorkbenchValidationRows(IReadOnlyList<RenderPipelineValidationRow> rows)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##WorkbenchValidationRows", 4, flags, Vector2.Zero))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Severity", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Problem", ImGuiTableColumnFlags.WidthStretch, 4f);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        // Each row is one line, so the clipper can skip rows that are not visible. The tooltip shows the full text.
        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, rows.Count, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawWorkbenchValidationRow(rows[i], i);
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }

        ImGui.EndTable();
    }

    private static void DrawWorkbenchValidationRow(RenderPipelineValidationRow row, int index)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        Vector4 color = row.Severity switch
        {
            ERenderPipelineProblemSeverity.Error => WorkbenchErrorColor,
            ERenderPipelineProblemSeverity.Warning => WorkbenchWarningColor,
            _ => WorkbenchInfoColor,
        };
        ImGui.TextColored(color, row.SeverityText);

        ImGui.TableNextColumn();
        ImGui.PushID(index);
        if (ImGui.Selectable(row.Message, false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick) && row.HasTarget)
            GoToWorkbenchProblem(row);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40f);
            ImGui.TextUnformatted(row.Message);
            ImGui.PopTextWrapPos();
            if (row.HasTarget)
                ImGui.TextDisabled("Click to go to the target.");
            ImGui.EndTooltip();
        }
        ImGui.PopID();

        ImGui.TableNextColumn();
        if (row.HasTarget)
            ImGui.TextColored(WorkbenchBadgeColor, row.TargetText);
        else
            ImGui.TextDisabled(row.TargetText);

        ImGui.TableNextColumn();
        ImGui.TextDisabled(row.RuleId);
    }

    private static void GoToWorkbenchProblem(RenderPipelineValidationRow row)
    {
        if (row.Target.Kind == ERenderPipelineWorkbenchSelectionKind.Stage)
            _workbench.SettingsShowPipeline = false;

        _workbench.Selection.Select(row.Target);
        if (row.TargetView is { } view)
            _workbench.RequestedView = view;
    }

    /// <summary>
    /// Draws the Script view: the read-only script text of the command chain. The text updates when the command
    /// chain changes.
    /// </summary>
    private static unsafe void DrawWorkbenchScriptView()
    {
        RenderPipelineWorkbenchScript script = _workbench.Script;
        script.Update(_workbench.Model);
        if (script.Error.Length > 0)
        {
            ImGui.TextColored(WorkbenchErrorColor, script.Error);
            return;
        }

        if (script.Text.Length == 0)
        {
            ImGui.TextDisabled("The pipeline has no command chain.");
            return;
        }

        if (ImGui.SmallButton("Copy"))
            ImGui.SetClipboardText(script.Text);
        ImGui.SameLine();
        ImGui.TextDisabled(script.LineCountText);
        ImGui.SameLine();
        ImGui.TextDisabled("Read-only. Editing comes with authoring support.");

        // The view draws only the visible lines, so a long script costs no more than one screen of text.
        ImGui.BeginChild("##WorkbenchScript", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        IReadOnlyList<string> lines = script.Lines;
        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, lines.Count, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    ImGui.TextUnformatted(lines[i]);
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }
        ImGui.EndChild();
    }
}
