using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>The draw alpha of outline rows and flow steps that did not run in the newest traced frame.</summary>
    private const float WorkbenchSkippedAlpha = 0.4f;

    private static readonly List<int> _workbenchVisibleOutlineRows = new(256);
    private static int _workbenchOutlineKeyBuild = -1;
    private static string _workbenchOutlineKeySearch = string.Empty;
    private static string _workbenchOutlineSearchLower = string.Empty;
    private static int _workbenchOutlineKeyFilters = -1;
    private static int _workbenchOutlineKeyCollapse = -1;
    private static string? _workbenchOutlineKeyResource;
    private static int _workbenchOutlineCollapseRevision;
    private static RenderPipelineWorkbenchModel? _workbenchOutlineKeyModel;

    private static void DrawWorkbenchOutline()
    {
        RenderPipelineWorkbenchModel model = _workbench.Model;

        string search = _workbench.OutlineSearch;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##WorkbenchOutlineSearch", "Search type, label, pass, or resource", ref search, 128))
            _workbench.OutlineSearch = search;

        bool gpuOnly = _workbench.OutlineGpuWorkOnly;
        if (ImGui.Checkbox("GPU work", ref gpuOnly))
            _workbench.OutlineGpuWorkOnly = gpuOnly;
        ImGui.SameLine();
        bool problemsOnly = _workbench.OutlineProblemsOnly;
        if (ImGui.Checkbox("Problems", ref problemsOnly))
            _workbench.OutlineProblemsOnly = problemsOnly;
        ImGui.SameLine();
        bool touches = _workbench.OutlineTouchesSelectedResource;
        if (ImGui.Checkbox("Touches resource", ref touches))
            _workbench.OutlineTouchesSelectedResource = touches;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only commands whose passes use the selected resource.");

        ImGui.Separator();
        UpdateWorkbenchVisibleOutlineRows(model);

        if (model.Outline.Count == 0)
        {
            ImGui.TextDisabled("The pipeline has no commands.");
            return;
        }

        ImGui.BeginChild("##WorkbenchOutlineRows", Vector2.Zero);
        DrawWorkbenchOutlineRows(model);
        ImGui.EndChild();
    }

    private static unsafe void DrawWorkbenchOutlineRows(RenderPipelineWorkbenchModel model)
    {
        RenderPipelineWorkbenchSelection selection = _workbench.Selection;
        RenderPipelineWorkbenchExecutionState execution = _workbench.Execution;
        IReadOnlySet<int> linked = _workbench.Links.LinkedNodes;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        float rowStartX = ImGui.GetCursorPosX();
        float badgeX = MathF.Max(rowStartX + 120f, ImGui.GetContentRegionAvail().X - 36f);
        float arrowWidth = ImGui.GetFrameHeight();
        uint linkedColor = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered, 0.35f);

        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, _workbenchVisibleOutlineRows.Count, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int visibleIndex = clipper.DisplayStart; visibleIndex < clipper.DisplayEnd; visibleIndex++)
                {
                    RenderPipelineOutlineRow row = model.Outline[_workbenchVisibleOutlineRows[visibleIndex]];
                    bool skipped = execution.Get(row.RowIndex) == ERenderPipelineRowExecution.Skipped;
                    if (skipped)
                        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, WorkbenchSkippedAlpha);
                    ImGui.PushID(row.NodeId);
                    ImGui.SetCursorPosX(rowStartX + (row.Depth * 12f));

                    if (row.HasChildren)
                    {
                        bool collapsed = _workbench.CollapsedOutlineNodes.Contains(row.NodeId);
                        if (ImGui.ArrowButton("##Toggle", collapsed ? ImGuiDir.Right : ImGuiDir.Down))
                        {
                            if (collapsed)
                                _workbench.CollapsedOutlineNodes.Remove(row.NodeId);
                            else
                                _workbench.CollapsedOutlineNodes.Add(row.NodeId);
                            _workbenchOutlineCollapseRevision++;
                        }
                    }
                    else
                    {
                        ImGui.Dummy(new Vector2(arrowWidth, arrowWidth));
                    }

                    ImGui.SameLine();
                    Vector2 swatchMin = ImGui.GetCursorScreenPos() + new Vector2(0f, 4f);
                    drawList.AddRectFilled(swatchMin, swatchMin + new Vector2(8f, 12f), GetWorkbenchCategoryColor(row.Category), 2f);
                    ImGui.Dummy(new Vector2(10f, 1f));
                    ImGui.SameLine();

                    bool selected = row.NodeId == selection.NodeId;
                    if (!selected && linked.Contains(row.NodeId))
                    {
                        Vector2 min = ImGui.GetCursorScreenPos();
                        drawList.AddRectFilled(min, min + new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight()), linkedColor);
                    }

                    if (row.Kind == ERenderPipelineOutlineRowKind.Container)
                        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
                    if (ImGui.Selectable(row.Label, selected, ImGuiSelectableFlags.AllowOverlap))
                        SelectWorkbenchRow(row);
                    if (row.Command is not null)
                        DrawWorkbenchCommandDragDrop(row.Command, row.Label);
                    if (row.Kind == ERenderPipelineOutlineRowKind.Container)
                        ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                    {
                        if (row.Command is not null)
                            DrawWorkbenchCommandTooltip(model, row, skipped);
                        else if (skipped)
                            ImGui.SetTooltip("This branch did not run in the newest traced frame.");
                    }

                    string timingBadge = _workbench.Timing.GetNodeBadge(row.NodeId);
                    if (timingBadge.Length > 0)
                    {
                        ImGui.SameLine(MathF.Max(rowStartX + 80f, badgeX - 64f));
                        ImGui.TextDisabled(timingBadge);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(_workbench.Timing.GetNodeTooltip(row.NodeId));
                    }

                    if (row.PassBadge.Length > 0 || row.HasProblem)
                    {
                        ImGui.SameLine(badgeX);
                        if (row.HasProblem)
                        {
                            ImGui.TextColored(row.HasGpuWorkWithoutPass ? WorkbenchErrorColor : WorkbenchWarningColor, "!");
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.HasGpuWorkWithoutPass
                                    ? "This command does GPU work but declares no render pass."
                                    : "This command type has no RenderCommandInfo attribute.");
                            ImGui.SameLine();
                        }

                        if (row.PassBadge.Length > 0)
                            ImGui.TextColored(WorkbenchBadgeColor, row.PassBadge);
                    }

                    ImGui.PopID();
                    if (skipped)
                        ImGui.PopStyleVar();
                }
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }
    }

    private static void DrawWorkbenchCommandTooltip(RenderPipelineWorkbenchModel model, RenderPipelineOutlineRow row, bool skipped)
    {
        ImGui.BeginTooltip();
        RenderCommandInfo info = RenderCommandInfo.Get(row.Command!);
        ImGui.TextUnformatted(info.DisplayName);
        if (info.Description.Length > 0)
            ImGui.TextDisabled(info.Description);
        if (skipped)
            ImGui.TextColored(WorkbenchWarningColor, "Did not run in the newest traced frame.");
        if (row.StructureIndex >= 0)
        {
            RenderPipelineStructureCommand entry = model.Structure.Commands[row.StructureIndex];
            for (int i = 0; i < entry.PassIndices.Count; i++)
            {
                if (model.PassGraph.NodeIndexByPass.TryGetValue(entry.PassIndices[i], out int nodeIndex))
                    ImGui.BulletText(model.PassGraph.Nodes[nodeIndex].Title);
            }
        }

        ImGui.EndTooltip();
    }

    private static void SelectWorkbenchRow(RenderPipelineOutlineRow row)
        => _workbench.Selection.Select(row.Kind == ERenderPipelineOutlineRowKind.Container
            ? RenderPipelineWorkbenchSelectionEntry.ForContainer(row.NodeId)
            : RenderPipelineWorkbenchSelectionEntry.ForCommand(row.NodeId));

    /// <summary>
    /// Rebuilds the visible row list when the model, the search text, the filters, the collapsed rows, or the
    /// selected resource change. Draw code then reads the list without allocating.
    /// </summary>
    private static void UpdateWorkbenchVisibleOutlineRows(RenderPipelineWorkbenchModel model)
    {
        int filters = (_workbench.OutlineGpuWorkOnly ? 1 : 0) |
            (_workbench.OutlineProblemsOnly ? 2 : 0) |
            (_workbench.OutlineTouchesSelectedResource ? 4 : 0);
        string? resource = _workbench.OutlineTouchesSelectedResource ? _workbench.Selection.ResourceName : null;

        if (ReferenceEquals(_workbenchOutlineKeyModel, model) &&
            _workbenchOutlineKeyBuild == model.OutlineBuildCount &&
            ReferenceEquals(_workbenchOutlineKeySearch, _workbench.OutlineSearch) &&
            _workbenchOutlineKeyFilters == filters &&
            _workbenchOutlineKeyCollapse == _workbenchOutlineCollapseRevision &&
            string.Equals(_workbenchOutlineKeyResource, resource, StringComparison.Ordinal))
        {
            return;
        }

        if (!ReferenceEquals(_workbenchOutlineKeySearch, _workbench.OutlineSearch))
            _workbenchOutlineSearchLower = _workbench.OutlineSearch.Trim().ToLowerInvariant();

        _workbenchOutlineKeyModel = model;
        _workbenchOutlineKeyBuild = model.OutlineBuildCount;
        _workbenchOutlineKeySearch = _workbench.OutlineSearch;
        _workbenchOutlineKeyFilters = filters;
        _workbenchOutlineKeyCollapse = _workbenchOutlineCollapseRevision;
        _workbenchOutlineKeyResource = resource;
        _workbenchVisibleOutlineRows.Clear();

        bool filtering = _workbenchOutlineSearchLower.Length > 0 || filters != 0;
        List<RenderPipelineOutlineRow> rows = model.Outline;
        for (int i = 0; i < rows.Count;)
        {
            RenderPipelineOutlineRow row = rows[i];
            if (filtering)
            {
                // Filtering flattens the tree: collapsed rows still match so search finds nested commands.
                if (MatchesWorkbenchOutlineFilter(model, row, resource))
                    _workbenchVisibleOutlineRows.Add(i);
                i++;
                continue;
            }

            _workbenchVisibleOutlineRows.Add(i);
            i = _workbench.CollapsedOutlineNodes.Contains(row.NodeId) ? Math.Max(i + 1, row.SubtreeEnd) : i + 1;
        }
    }

    private static bool MatchesWorkbenchOutlineFilter(RenderPipelineWorkbenchModel model, RenderPipelineOutlineRow row, string? resource)
    {
        if (row.Kind == ERenderPipelineOutlineRowKind.Container)
            return _workbenchOutlineSearchLower.Length > 0 &&
                   !_workbench.OutlineGpuWorkOnly &&
                   !_workbench.OutlineProblemsOnly &&
                   resource is null &&
                   row.SearchText.Contains(_workbenchOutlineSearchLower, StringComparison.Ordinal);

        if (_workbenchOutlineSearchLower.Length > 0 && !row.SearchText.Contains(_workbenchOutlineSearchLower, StringComparison.Ordinal))
            return false;

        if (_workbench.OutlineGpuWorkOnly && row.Command is not null && !RenderCommandInfo.Get(row.Command).DoesGpuWork && row.PassCount == 0)
            return false;

        if (_workbench.OutlineProblemsOnly && !row.HasProblem)
            return false;

        if (_workbench.OutlineTouchesSelectedResource)
        {
            if (resource is null || row.StructureIndex < 0)
                return false;

            RenderPipelineStructureCommand entry = model.Structure.Commands[row.StructureIndex];
            bool touches = false;
            for (int i = 0; i < entry.PassIndices.Count && !touches; i++)
            {
                if (model.PassGraph.NodeIndexByPass.TryGetValue(entry.PassIndices[i], out int nodeIndex))
                    touches = RenderPipelineWorkbenchLinks.UsesResource(model.PassGraph.Nodes[nodeIndex].Metadata, resource);
            }

            if (!touches)
                return false;
        }

        return true;
    }

    /// <summary>Returns the swatch color of a command category.</summary>
    private static uint GetWorkbenchCategoryColor(ERenderCommandCategory category)
        => category switch
        {
            ERenderCommandCategory.Flow => 0xFF4FA3E0u,
            ERenderCommandCategory.State => 0xFF8C8C8Cu,
            ERenderCommandCategory.Targets => 0xFF4FC3A1u,
            ERenderCommandCategory.Scene => 0xFF3D9BF2u,
            ERenderCommandCategory.Fullscreen => 0xFFD08A4Fu,
            ERenderCommandCategory.PostProcess => 0xFFE06FA8u,
            ERenderCommandCategory.AntiAliasing => 0xFFB37FE0u,
            ERenderCommandCategory.Lighting => 0xFF4FD8F2u,
            ERenderCommandCategory.Compute => 0xFF3FA0FFu,
            ERenderCommandCategory.Transfer => 0xFF7FD87Fu,
            ERenderCommandCategory.Synchronization => 0xFF6F6FE0u,
            ERenderCommandCategory.Debug => 0xFF5FB0B0u,
            ERenderCommandCategory.Advanced => 0xFFE0C04Fu,
            ERenderCommandCategory.Utility => 0xFFA0A0A0u,
            _ => 0xFF3030E0u,
        };

    /// <summary>Returns the color of a pass stage.</summary>
    private static uint GetWorkbenchStageColor(ERenderGraphPassStage stage)
        => stage switch
        {
            ERenderGraphPassStage.Graphics => 0xFFE0A050u,
            ERenderGraphPassStage.Compute => 0xFF4FA0F0u,
            ERenderGraphPassStage.Transfer => 0xFF70D070u,
            _ => 0xFFA0A0A0u,
        };
}
