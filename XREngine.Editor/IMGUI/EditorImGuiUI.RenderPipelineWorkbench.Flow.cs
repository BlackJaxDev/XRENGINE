using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const float FlowLaneHeaderWidth = 170f;
    private const float FlowOrderStep = 220f;
    private const float FlowLaneHeight = 110f;
    private const float FlowStepWidth = 170f;
    private const float FlowStepHeight = 58f;
    private const float FlowStepYOffset = 38f;
    private const int FlowMaxPassChips = 6;

    private static void DrawWorkbenchFlowView()
    {
        RenderPipelineWorkbenchModel model = _workbench.Model;
        RenderPipelineTimelineLayout layout = model.Timeline;
        ImGui.TextDisabled("Lanes run branches. Pan: right or middle drag. Zoom: wheel.");
        if (layout.Steps.Count == 0)
        {
            ImGui.TextDisabled("The pipeline has no commands.");
            return;
        }

        RequestWorkbenchFlowFocus(model);
        bool began = BeginWorkbenchCanvas("##WorkbenchFlowCanvas", _workbench.FlowView, out ImDrawListPtr drawList, out Vector2 canvasPos, out Vector2 canvasSize, out _);
        if (began)
        {
            DrawWorkbenchCanvasGrid(drawList, canvasPos, canvasSize, _workbench.FlowView);
            DrawWorkbenchFlowLanes(drawList, canvasPos, canvasSize, layout);
            DrawWorkbenchFlowConnectors(drawList, canvasPos, layout);
            DrawWorkbenchFlowSteps(drawList, canvasPos, canvasSize, model, layout);
            DrawWorkbenchFlowOverlays(drawList, canvasPos, canvasSize, model, layout);
        }

        EndWorkbenchCanvas(began);
    }

    /// <summary>Scrolls the Flow canvas to the selected command when another view selected it.</summary>
    private static void RequestWorkbenchFlowFocus(RenderPipelineWorkbenchModel model)
    {
        int nodeId = _workbench.Selection.NodeId;
        if (nodeId == 0 || nodeId == _workbenchFlowFocusedNode)
            return;

        _workbenchFlowFocusedNode = nodeId;
        if (model.Timeline.StepById.TryGetValue(nodeId, out RenderPipelineTimelineStep? step))
            _workbench.FlowView.PendingFocus = GetFlowStepMin(step) + new Vector2(FlowStepWidth * 0.5f, FlowStepHeight * 0.5f);
    }

    private static int _workbenchFlowFocusedNode;

    private static Vector2 GetFlowStepMin(RenderPipelineTimelineStep step)
        => new(FlowLaneHeaderWidth + (step.OrderIndex * FlowOrderStep), (step.LaneIndex * FlowLaneHeight) + FlowStepYOffset);

    private static void DrawWorkbenchFlowLanes(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, RenderPipelineTimelineLayout layout)
    {
        RenderPipelineCanvasViewState view = _workbench.FlowView;
        uint laneColor = ImGui.GetColorU32(ImGuiCol.ChildBg, 0.9f);
        uint headerColor = ImGui.GetColorU32(ImGuiCol.TitleBg);
        uint borderColor = ImGui.GetColorU32(ImGuiCol.Border);
        uint selectedColor = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);
        uint textColor = ImGui.GetColorU32(ImGuiCol.Text);
        uint subTextColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        int selectedNode = _workbench.Selection.NodeId;

        for (int i = 0; i < layout.Lanes.Count; i++)
        {
            RenderPipelineTimelineLane lane = layout.Lanes[i];
            float top = lane.LaneIndex * FlowLaneHeight;
            Vector2 min = view.ToScreen(canvasPos, new Vector2(0f, top + 8f));
            Vector2 max = view.ToScreen(canvasPos, new Vector2(FlowLaneHeaderWidth + (lane.EndOrderExclusive * FlowOrderStep), top + FlowLaneHeight - 12f));
            if (!IsWorkbenchRectVisible(min, max, canvasPos, canvasSize))
                continue;

            drawList.AddRectFilled(min, max, laneColor, 8f * view.Zoom);
            drawList.AddRect(min, max, lane.Id == selectedNode ? selectedColor : borderColor, 8f * view.Zoom, ImDrawFlags.None, 1.5f);
            Vector2 headerMax = view.ToScreen(canvasPos, new Vector2(FlowLaneHeaderWidth, top + FlowLaneHeight - 12f));
            drawList.AddRectFilled(min, headerMax, headerColor, 8f * view.Zoom, ImDrawFlags.RoundCornersLeft);
            drawList.AddText(min + (new Vector2(10f, 6f) * view.Zoom), textColor, lane.Label);
            drawList.AddText(min + (new Vector2(10f, 28f) * view.Zoom), subTextColor, lane.Detail);

            ImGui.SetCursorScreenPos(min);
            ImGui.PushID(lane.Id);
            ImGui.InvisibleButton("##Lane", Vector2.Max(headerMax - min, Vector2.One));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForContainer(lane.Id));
            ImGui.PopID();
        }
    }

    private static void DrawWorkbenchFlowConnectors(ImDrawListPtr drawList, Vector2 canvasPos, RenderPipelineTimelineLayout layout)
    {
        RenderPipelineCanvasViewState view = _workbench.FlowView;
        uint sequenceColor = ImGui.GetColorU32(ImGuiCol.PlotLines);
        uint branchColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

        for (int laneIndex = 0; laneIndex < layout.Lanes.Count; laneIndex++)
        {
            RenderPipelineTimelineLane lane = layout.Lanes[laneIndex];
            List<RenderPipelineTimelineStep> steps = lane.Steps;
            for (int i = 0; i < steps.Count - 1; i++)
            {
                Vector2 start = view.ToScreen(canvasPos, GetFlowStepMin(steps[i]) + new Vector2(FlowStepWidth, FlowStepHeight * 0.5f));
                Vector2 end = view.ToScreen(canvasPos, GetFlowStepMin(steps[i + 1]) + new Vector2(0f, FlowStepHeight * 0.5f));
                drawList.AddLine(start, end, sequenceColor, 2f);
            }

            if (lane.ParentCommandId is not int parentId || !layout.StepById.TryGetValue(parentId, out RenderPipelineTimelineStep? parent))
                continue;

            Vector2 parentAnchor = view.ToScreen(canvasPos, GetFlowStepMin(parent) + new Vector2(FlowStepWidth * 0.5f, FlowStepHeight));
            Vector2 childAnchor = view.ToScreen(canvasPos, new Vector2(FlowLaneHeaderWidth + (lane.StartOrder * FlowOrderStep), (lane.LaneIndex * FlowLaneHeight) + FlowStepYOffset + (FlowStepHeight * 0.5f)));
            float elbowX = MathF.Max(parentAnchor.X + (20f * view.Zoom), childAnchor.X - (30f * view.Zoom));
            Vector2 elbowA = new(elbowX, parentAnchor.Y + (12f * view.Zoom));
            Vector2 elbowB = new(elbowX, childAnchor.Y);
            drawList.AddLine(parentAnchor, elbowA, branchColor, 2f);
            drawList.AddLine(elbowA, elbowB, branchColor, 2f);
            drawList.AddLine(elbowB, childAnchor, branchColor, 2f);
        }
    }

    private static void DrawWorkbenchFlowSteps(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, RenderPipelineWorkbenchModel model, RenderPipelineTimelineLayout layout)
    {
        RenderPipelineCanvasViewState view = _workbench.FlowView;
        uint blockColor = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint branchBlockColor = ImGui.GetColorU32(ImGuiCol.Header);
        uint borderColor = ImGui.GetColorU32(ImGuiCol.Border);
        uint selectedColor = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);
        uint linkedColor = ImGui.GetColorU32(ImGuiCol.PlotHistogram);
        uint resumeColor = ImGui.GetColorU32(ImGuiCol.TabDimmed);
        uint textColor = ImGui.GetColorU32(ImGuiCol.Text);
        uint subTextColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        uint problemColor = ImGui.GetColorU32(WorkbenchErrorColor);
        uint skippedVeil = ImGui.GetColorU32(ImGuiCol.WindowBg, 1f - WorkbenchSkippedAlpha);
        RenderPipelineWorkbenchExecutionState execution = _workbench.Execution;
        int selectedNode = _workbench.Selection.NodeId;
        IReadOnlySet<int> linked = _workbench.Links.LinkedNodes;
        float rounding = 8f * view.Zoom;
        bool showText = view.Zoom >= 0.45f;

        for (int i = 0; i < layout.Steps.Count; i++)
        {
            RenderPipelineTimelineStep step = layout.Steps[i];
            Vector2 worldMin = GetFlowStepMin(step);
            Vector2 min = view.ToScreen(canvasPos, worldMin);
            Vector2 max = view.ToScreen(canvasPos, worldMin + new Vector2(FlowStepWidth, FlowStepHeight));
            Vector2 resumeMax = max;
            if (step.ResumeOrderExclusive > step.OrderIndex + 1)
                resumeMax = view.ToScreen(canvasPos, new Vector2(FlowLaneHeaderWidth + (step.ResumeOrderExclusive * FlowOrderStep) - 24f, worldMin.Y + FlowStepHeight - 6f));
            if (!IsWorkbenchRectVisible(min, Vector2.Max(max, resumeMax), canvasPos, canvasSize))
                continue;

            if (step.ResumeOrderExclusive > step.OrderIndex + 1)
                drawList.AddRectFilled(min + (new Vector2(FlowStepWidth + 6f, 18f) * view.Zoom), resumeMax, resumeColor, 6f * view.Zoom);

            bool selected = step.Id == selectedNode;
            bool isLinked = !selected && linked.Contains(step.Id);
            drawList.AddRectFilled(min, max, step.HasBranches ? branchBlockColor : blockColor, rounding);
            drawList.AddRectFilled(min, new Vector2(min.X + (4f * view.Zoom), max.Y), GetWorkbenchCategoryColor(step.Category), rounding, ImDrawFlags.RoundCornersLeft);
            drawList.AddRect(min, max, selected ? selectedColor : isLinked ? linkedColor : borderColor, rounding, ImDrawFlags.None, selected || isLinked ? 3f : 1.5f);

            RenderPipelineStructureCommand? entry = model.TryGetStructureCommand(step.Id, out RenderPipelineStructureCommand found) ? found : null;
            if (showText)
            {
                drawList.AddText(min + (new Vector2(10f, 6f) * view.Zoom), textColor, step.Title);
                drawList.AddText(min + (new Vector2(10f, 26f) * view.Zoom), subTextColor, step.IndexLabel);
                if (step.HasBranches)
                    drawList.AddText(min + (new Vector2(40f, 26f) * view.Zoom), subTextColor, step.BranchSummary);
                if (entry is { IsGpuWorkWithoutPass: true })
                    drawList.AddText(new Vector2(max.X - (14f * view.Zoom), min.Y + (4f * view.Zoom)), problemColor, "!");
            }

            if (entry is not null)
                DrawWorkbenchFlowPassChips(drawList, model, entry, min, max, view.Zoom);

            // A veil over the block shows that the command did not run in the newest traced frame.
            if (model.OutlineRowByNodeId.TryGetValue(step.Id, out int rowIndex) &&
                execution.Get(rowIndex) == ERenderPipelineRowExecution.Skipped)
            {
                drawList.AddRectFilled(min, max, skippedVeil, rounding);
            }

            ImGui.SetCursorScreenPos(min);
            ImGui.PushID(step.Id);
            ImGui.InvisibleButton("##Step", Vector2.Max(max - min, Vector2.One));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForCommand(step.Id));
            ImGui.PopID();
        }
    }

    /// <summary>Draws one small chip for each pass of a command, colored by pass stage.</summary>
    private static void DrawWorkbenchFlowPassChips(ImDrawListPtr drawList, RenderPipelineWorkbenchModel model, RenderPipelineStructureCommand entry, Vector2 min, Vector2 max, float zoom)
    {
        int count = Math.Min(entry.PassIndices.Count, FlowMaxPassChips);
        if (count == 0)
            return;

        float chipWidth = 14f * zoom;
        float chipHeight = 7f * zoom;
        float gap = 3f * zoom;
        Vector2 cursor = new(min.X + (10f * zoom), max.Y - chipHeight - (5f * zoom));
        int selectedPass = _workbench.Selection.PassIndex;
        uint selectedBorder = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);
        for (int i = 0; i < count; i++)
        {
            int passIndex = entry.PassIndices[i];
            uint color = model.PassGraph.NodeIndexByPass.TryGetValue(passIndex, out int nodeIndex)
                ? GetWorkbenchStageColor(model.PassGraph.Nodes[nodeIndex].Metadata.Stage)
                : 0xFF808080u;
            Vector2 chipMax = cursor + new Vector2(chipWidth, chipHeight);
            drawList.AddRectFilled(cursor, chipMax, color, 2f * zoom);
            if (passIndex == selectedPass)
                drawList.AddRect(cursor, chipMax, selectedBorder, 2f * zoom, ImDrawFlags.None, 2f);
            cursor.X += chipWidth + gap;
        }

        if (entry.PassIndices.Count > FlowMaxPassChips)
            drawList.AddText(cursor, ImGui.GetColorU32(ImGuiCol.TextDisabled), "+");
    }
}
