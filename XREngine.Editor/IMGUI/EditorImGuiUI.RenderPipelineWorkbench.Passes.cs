using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static bool[] _workbenchPassFocus = [];
    private static readonly Stack<int> _workbenchPassFocusScratch = new();
    private static int _workbenchPassFocusKeyPass = int.MinValue;
    private static int _workbenchPassFocusKeyBuild = -1;
    private static int _workbenchPassesFocusedPass = int.MinValue;

    private static void DrawWorkbenchPassesView()
    {
        RenderPipelineWorkbenchModel model = _workbench.Model;
        RenderPipelinePassGraphLayout graph = model.PassGraph;

        bool tableMode = _workbench.PassTableMode;
        if (ImGui.RadioButton("Graph", !tableMode))
            _workbench.PassTableMode = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Table", tableMode))
            _workbench.PassTableMode = true;
        ImGui.SameLine();
        bool focus = _workbench.PassFocusMode;
        if (ImGui.Checkbox("Focus", ref focus))
            _workbench.PassFocusMode = focus;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dim passes that are not ancestors or descendants of the selected pass.");
        ImGui.SameLine();
        DrawWorkbenchPassLegend();

        if (graph.Error is { } error)
            ImGui.TextColored(WorkbenchErrorColor, error);

        if (graph.Nodes.Count == 0)
        {
            ImGui.TextDisabled("The pipeline declares no render passes.");
            return;
        }

        UpdateWorkbenchPassFocus(model);
        if (_workbench.PassTableMode)
            DrawWorkbenchPassTable(model);
        else
            DrawWorkbenchPassGraph(model);
    }

    private static void DrawWorkbenchPassLegend()
    {
        ImGui.TextDisabled("Edges:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "read after write (solid)");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.4f, 1f), "write hazards (dashed)");
        ImGui.SameLine();
        ImGui.TextDisabled("explicit (dotted)");
    }

    private static void UpdateWorkbenchPassFocus(RenderPipelineWorkbenchModel model)
    {
        RenderPipelinePassGraphLayout graph = model.PassGraph;
        int selectedPass = _workbench.Selection.PassIndex;
        if (_workbenchPassFocusKeyPass == selectedPass && _workbenchPassFocusKeyBuild == model.PassGraphBuildCount && _workbenchPassFocus.Length == graph.Nodes.Count)
            return;

        _workbenchPassFocusKeyPass = selectedPass;
        _workbenchPassFocusKeyBuild = model.PassGraphBuildCount;
        if (_workbenchPassFocus.Length != graph.Nodes.Count)
            _workbenchPassFocus = new bool[graph.Nodes.Count];

        if (graph.NodeIndexByPass.TryGetValue(selectedPass, out int nodeIndex))
            graph.CollectRelated(nodeIndex, _workbenchPassFocus, _workbenchPassFocusScratch);
        else
            Array.Fill(_workbenchPassFocus, true);
    }

    private static void DrawWorkbenchPassGraph(RenderPipelineWorkbenchModel model)
    {
        RenderPipelinePassGraphLayout graph = model.PassGraph;
        RequestWorkbenchPassesFocus(graph);
        bool began = BeginWorkbenchCanvas("##WorkbenchPassesCanvas", _workbench.PassesView, out ImDrawListPtr drawList, out Vector2 canvasPos, out Vector2 canvasSize, out _);
        if (began)
        {
            DrawWorkbenchCanvasGrid(drawList, canvasPos, canvasSize, _workbench.PassesView);
            DrawWorkbenchPassBands(drawList, canvasPos, graph);
            DrawWorkbenchPassEdges(drawList, canvasPos, canvasSize, graph);
            DrawWorkbenchPassNodes(drawList, canvasPos, canvasSize, graph);
        }

        EndWorkbenchCanvas(began);
    }

    private static void RequestWorkbenchPassesFocus(RenderPipelinePassGraphLayout graph)
    {
        int passIndex = _workbench.Selection.PassIndex;
        if (passIndex < 0 || passIndex == _workbenchPassesFocusedPass)
            return;

        _workbenchPassesFocusedPass = passIndex;
        if (graph.NodeIndexByPass.TryGetValue(passIndex, out int nodeIndex))
        {
            RenderPipelinePassGraphNode node = graph.Nodes[nodeIndex];
            _workbench.PassesView.PendingFocus = node.Position + new Vector2(RenderPipelinePassGraphLayout.NodeWidth * 0.5f, RenderPipelinePassGraphLayout.NodeHeight * 0.5f);
        }
    }

    private static void DrawWorkbenchPassBands(ImDrawListPtr drawList, Vector2 canvasPos, RenderPipelinePassGraphLayout graph)
    {
        RenderPipelineCanvasViewState view = _workbench.PassesView;
        uint textColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        float width = MathF.Max(graph.CanvasSize.X, RenderPipelinePassGraphLayout.NodeWidth) + 40f;
        for (int i = 0; i < graph.Bands.Count; i++)
        {
            (ERenderGraphPassStage stage, float top, float height, string label) = graph.Bands[i];
            Vector2 min = view.ToScreen(canvasPos, new Vector2(-20f, top));
            Vector2 max = view.ToScreen(canvasPos, new Vector2(width, top + height));
            drawList.AddRectFilled(min, max, (GetWorkbenchStageColor(stage) & 0x00FFFFFFu) | 0x18000000u, 6f * view.Zoom);
            drawList.AddText(min + (new Vector2(6f, 4f) * view.Zoom), textColor, label);
        }
    }

    private static void DrawWorkbenchPassEdges(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, RenderPipelinePassGraphLayout graph)
    {
        RenderPipelineCanvasViewState view = _workbench.PassesView;
        uint rawColor = ImGui.GetColorU32(new Vector4(0.6f, 0.85f, 1f, 0.9f));
        uint hazardColor = ImGui.GetColorU32(new Vector4(1f, 0.7f, 0.4f, 0.9f));
        uint explicitColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        uint dimMask = 0x40FFFFFFu;
        bool focus = _workbench.PassFocusMode && _workbench.Selection.PassIndex >= 0;
        float half = RenderPipelinePassGraphLayout.NodeHeight * 0.5f;

        for (int i = 0; i < graph.Edges.Count; i++)
        {
            RenderPipelinePassGraphEdge edge = graph.Edges[i];
            RenderPipelinePassGraphNode from = graph.Nodes[edge.FromNode];
            RenderPipelinePassGraphNode to = graph.Nodes[edge.ToNode];
            Vector2 a = view.ToScreen(canvasPos, from.Position + new Vector2(RenderPipelinePassGraphLayout.NodeWidth, half));
            Vector2 b = view.ToScreen(canvasPos, to.Position + new Vector2(0f, half));
            if (!IsWorkbenchRectVisible(Vector2.Min(a, b), Vector2.Max(a, b), canvasPos, canvasSize))
                continue;

            float dx = MathF.Max(40f * view.Zoom, MathF.Abs(b.X - a.X) * 0.5f);
            Vector2 c1 = a + new Vector2(dx, 0f);
            Vector2 c2 = b - new Vector2(dx, 0f);
            uint color = edge.Kind switch
            {
                ERenderPipelinePassEdgeKind.ReadAfterWrite => rawColor,
                ERenderPipelinePassEdgeKind.Explicit => explicitColor,
                _ => hazardColor,
            };
            if (focus && !(_workbenchPassFocus[edge.FromNode] && _workbenchPassFocus[edge.ToNode]))
                color &= dimMask;

            switch (edge.Kind)
            {
                case ERenderPipelinePassEdgeKind.ReadAfterWrite:
                    drawList.AddBezierCubic(a, c1, c2, b, color, 2f);
                    break;
                case ERenderPipelinePassEdgeKind.Explicit:
                    DrawWorkbenchPatternedBezier(drawList, a, c1, c2, b, color, 1.5f, dotted: true);
                    break;
                default:
                    DrawWorkbenchPatternedBezier(drawList, a, c1, c2, b, color, 2f, dotted: false);
                    break;
            }
        }
    }

    private static void DrawWorkbenchPassNodes(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, RenderPipelinePassGraphLayout graph)
    {
        RenderPipelineCanvasViewState view = _workbench.PassesView;
        uint nodeColor = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint borderColor = ImGui.GetColorU32(ImGuiCol.Border);
        uint selectedColor = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);
        uint linkedColor = ImGui.GetColorU32(ImGuiCol.PlotHistogram);
        uint textColor = ImGui.GetColorU32(ImGuiCol.Text);
        uint subTextColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        int selectedPass = _workbench.Selection.PassIndex;
        IReadOnlySet<int> linked = _workbench.Links.LinkedPasses;
        bool focus = _workbench.PassFocusMode && selectedPass >= 0;
        bool showText = view.Zoom >= 0.45f;
        float rounding = 8f * view.Zoom;
        Vector2 size = new(RenderPipelinePassGraphLayout.NodeWidth, RenderPipelinePassGraphLayout.NodeHeight);

        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            RenderPipelinePassGraphNode node = graph.Nodes[i];
            Vector2 min = view.ToScreen(canvasPos, node.Position);
            Vector2 max = min + (size * view.Zoom);
            if (!IsWorkbenchRectVisible(min, max, canvasPos, canvasSize))
                continue;

            bool dim = focus && !_workbenchPassFocus[i];
            uint alphaMask = dim ? 0x50FFFFFFu : 0xFFFFFFFFu;
            bool selected = node.PassIndex == selectedPass;
            bool isLinked = !selected && linked.Contains(node.PassIndex);
            uint stageColor = GetWorkbenchStageColor(node.Metadata.Stage);

            drawList.AddRectFilled(min, max, nodeColor & alphaMask, rounding);
            drawList.AddRectFilled(min, new Vector2(max.X, min.Y + (24f * view.Zoom)), stageColor & ((alphaMask & 0xFF000000u) | 0x00FFFFFFu) & 0xA0FFFFFFu, rounding, ImDrawFlags.RoundCornersTop);
            drawList.AddRect(min, max, (selected ? selectedColor : isLinked ? linkedColor : borderColor) & alphaMask, rounding, ImDrawFlags.None, selected || isLinked ? 3f : 1.5f);
            if (showText)
            {
                drawList.AddText(min + (new Vector2(8f, 4f) * view.Zoom), textColor & alphaMask, node.Title);
                drawList.AddText(min + (new Vector2(8f, 30f) * view.Zoom), subTextColor & alphaMask, node.Subtitle);
                drawList.AddText(min + (new Vector2(8f, 52f) * view.Zoom), subTextColor & alphaMask, node.AttachmentSummary);
                string timingBadge = _workbench.Timing.GetPassBadge(node.PassIndex);
                if (timingBadge.Length > 0)
                    drawList.AddText(new Vector2(max.X - (64f * view.Zoom), min.Y + (4f * view.Zoom)), textColor & alphaMask, timingBadge);
            }

            ImGui.SetCursorScreenPos(min);
            ImGui.PushID(node.PassIndex);
            ImGui.InvisibleButton("##Pass", Vector2.Max(max - min, Vector2.One));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForPass(node.PassIndex));
            if (ImGui.IsItemHovered() && node.ProducersText.Length + node.ConsumersText.Length > 0)
            {
                ImGui.BeginTooltip();
                if (node.ProducersText.Length > 0)
                {
                    ImGui.TextDisabled("Depends on:");
                    ImGui.TextUnformatted(node.ProducersText);
                }

                if (node.ConsumersText.Length > 0)
                {
                    ImGui.TextDisabled("Used by:");
                    ImGui.TextUnformatted(node.ConsumersText);
                }

                ImGui.EndTooltip();
            }

            ImGui.PopID();
        }
    }

    private static unsafe void DrawWorkbenchPassTable(RenderPipelineWorkbenchModel model)
    {
        RenderPipelinePassGraphLayout graph = model.PassGraph;
        const ImGuiTableFlags Flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##WorkbenchPassTable", 5, Flags))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("Pass");
        ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Producers");
        ImGui.TableSetupColumn("Consumers");
        ImGui.TableHeadersRow();

        int selectedPass = _workbench.Selection.PassIndex;
        bool focus = _workbench.PassFocusMode && selectedPass >= 0;
        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, graph.Nodes.Count, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    RenderPipelinePassGraphNode node = graph.Nodes[i];
                    bool dim = focus && !_workbenchPassFocus[i];
                    ImGui.TableNextRow();
                    ImGui.PushID(node.PassIndex);
                    if (dim)
                        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));

                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(node.OrderText);
                    ImGui.TableSetColumnIndex(1);
                    if (ImGui.Selectable(node.Title, node.PassIndex == selectedPass, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                        _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForPass(node.PassIndex));
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(node.Subtitle);
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextWrapped(node.ProducersText);
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextWrapped(node.ConsumersText);

                    if (dim)
                        ImGui.PopStyleColor();
                    ImGui.PopID();
                }
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }

        ImGui.EndTable();
    }
}
