using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Core.Tools;
using XREngine.Rendering.Shaders.Generator;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private sealed class ShaderGraphViewState
    {
        public Vector2 Pan = new(40, 40);
        public float Zoom = 1.0f;
        public int? SelectedNodeId;
        public readonly Dictionary<int, Vector2> NodePositions = new();
        public bool HasLayout;
    }

    private static ShaderGraph? _activeShaderGraph;
    private static readonly ShaderGraphViewState _shaderGraphView = new();
    private static string _shaderGraphSource = "";
    private static string _shaderGraphGenerated = "";
    private static string _shaderGraphError = "";

    private static void DrawShaderGraphPanel()
    {
        if (!_showShaderGraphPanel)
            return;

        ImGui.SetNextWindowSize(new Vector2(1150, 780), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Shader Graph", ref _showShaderGraphPanel, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        DrawShaderGraphMenuBar();
        DrawShaderGraphBody();

        ImGui.End();
    }

    private static void DrawShaderGraphMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.MenuItem("Deconstruct", null, false, !string.IsNullOrWhiteSpace(_shaderGraphSource)))
            TryBuildShaderGraphFromSource();

        if (ImGui.MenuItem("Generate", null, false, _activeShaderGraph is not null))
            TryGenerateShaderFromGraph();

        if (ImGui.MenuItem("Auto Layout", null, false, _activeShaderGraph is not null))
            AutoLayoutShaderGraph();

        if (ImGui.MenuItem("Reset View", null, false, _activeShaderGraph is not null))
        {
            _shaderGraphView.Pan = new Vector2(40, 40);
            _shaderGraphView.Zoom = 1.0f;
        }

        ImGui.EndMenuBar();
    }

    private static void DrawShaderGraphBody()
    {
        ImGui.Text("GLSL Source");
        ImGui.InputTextMultiline("##ShaderGraphSource", ref _shaderGraphSource, (uint)(1 << 18), new Vector2(-1, 170), ImGuiInputTextFlags.AllowTabInput);

        if (!string.IsNullOrWhiteSpace(_shaderGraphError))
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), _shaderGraphError);

        if (ImGui.Button("Deconstruct to Nodes"))
            TryBuildShaderGraphFromSource();

        ImGui.SameLine();
        bool canGenerate = _activeShaderGraph is not null;
        if (ImGui.Button("Generate GLSL") && canGenerate)
            TryGenerateShaderFromGraph();

        ImGui.Separator();

        float sidebarWidth = 300f;
        float inspectorHeight = 170f;

        ImGui.BeginChild("ShaderGraphSidebar", new Vector2(sidebarWidth, -inspectorHeight - ImGui.GetStyle().ItemSpacing.Y), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
        DrawShaderGraphSidebar();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("ShaderGraphCanvasRegion", new Vector2(0, -inspectorHeight - ImGui.GetStyle().ItemSpacing.Y), ImGuiChildFlags.Border);
        DrawShaderGraphCanvas();
        ImGui.EndChild();

        ImGui.Separator();
        ImGui.Text("Node Inspector");
        ImGui.BeginChild("ShaderGraphInspector", new Vector2(0, inspectorHeight), ImGuiChildFlags.Border);
        DrawShaderGraphInspector();
        ImGui.EndChild();

        ImGui.Separator();
        ImGui.Text("Generated GLSL");
        ImGui.InputTextMultiline("##ShaderGraphGenerated", ref _shaderGraphGenerated, (uint)(1 << 18), new Vector2(-1, 180), ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
    }

    private static void DrawShaderGraphSidebar()
    {
        if (_activeShaderGraph is null)
        {
            ImGui.TextDisabled("Load or paste a GLSL shader to build a node graph.");
            return;
        }

        ImGui.TextDisabled("Attributes");
        foreach (var attr in _activeShaderGraph.Attributes)
            ImGui.BulletText($"{attr.TypeName} {attr.Name}");

        ImGui.Separator();
        ImGui.TextDisabled("Uniforms");
        foreach (var uniform in _activeShaderGraph.Uniforms)
            ImGui.BulletText($"{uniform.TypeName} {uniform.Name}");

        ImGui.Separator();
        ImGui.TextDisabled("Constants");
        foreach (var constant in _activeShaderGraph.Consts)
            ImGui.BulletText($"{constant.TypeName} {constant.Name} = {constant.DefaultValue}");

        ImGui.Separator();
        ImGui.TextDisabled("Outputs");
        foreach (var output in _activeShaderGraph.Outputs)
            ImGui.BulletText($"{output.TypeName} {output.Name}");

        ImGui.Separator();
        ImGui.TextDisabled("Methods");
        foreach (var method in _activeShaderGraph.Methods.Where(m => !m.IsMain))
        {
            ImGui.Selectable($"{method.Name} ({method.Parameters.Count} params)", false);
            ImGui.SameLine();
            ImGui.PushID(method.Name);
            if (ImGui.SmallButton("Add Node"))
            {
                var node = _activeShaderGraph.AddMethodInvocationNode(method);
                _shaderGraphView.SelectedNodeId = node.Id;
                _shaderGraphView.HasLayout = false;
            }
            ImGui.PopID();
        }

        ImGui.Separator();
        if (ImGui.Button("Auto Layout"))
            AutoLayoutShaderGraph();
        if (ImGui.Button("Clear Graph"))
        {
            _activeShaderGraph = null;
            _shaderGraphGenerated = "";
            _shaderGraphError = "";
            _shaderGraphView.NodePositions.Clear();
            _shaderGraphView.SelectedNodeId = null;
            _shaderGraphView.HasLayout = false;
        }
    }

    private static void DrawShaderGraphCanvas()
    {
        var graph = _activeShaderGraph;
        if (graph is null)
        {
            ImGui.TextDisabled("No shader graph available.");
            return;
        }

        if (!_shaderGraphView.HasLayout || !_shaderGraphView.NodePositions.Keys.ToHashSet().SetEquals(graph.Nodes.Select(n => n.Id)))
        {
            AutoLayoutShaderGraph(graph, _shaderGraphView);
            _shaderGraphView.HasLayout = true;
        }

        var io = ImGui.GetIO();
        ImGui.BeginChild("##ShaderGraphCanvas", new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();

        if (canvasSize.X < 1 || canvasSize.Y < 1)
        {
            ImGui.EndChild();
            return;
        }

        ImGui.InvisibleButton("##ShaderGraphCanvasBtn", canvasSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);

        bool canvasHovered = ImGui.IsItemHovered();

        if (canvasHovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0f) || ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f) || (ImGui.IsKeyDown(ImGuiKey.Space) && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))))
            _shaderGraphView.Pan += io.MouseDelta / MathF.Max(1e-6f, _shaderGraphView.Zoom);

        if (canvasHovered && Math.Abs(io.MouseWheel) > 1e-6f)
        {
            float prevZoom = _shaderGraphView.Zoom;
            float zoomBase = io.KeyCtrl ? 1.15f : 1.07f;
            float nextZoom = Math.Clamp(_shaderGraphView.Zoom * (float)Math.Pow(zoomBase, io.MouseWheel), 0.25f, 2.5f);
            if (Math.Abs(nextZoom - prevZoom) > 1e-6f)
            {
                var mouse = io.MousePos;
                Vector2 mouseLocal = mouse - canvasPos;
                Vector2 worldBefore = (mouseLocal / prevZoom) - _shaderGraphView.Pan;
                _shaderGraphView.Zoom = nextZoom;
                Vector2 worldAfter = (mouseLocal / nextZoom) - _shaderGraphView.Pan;
                _shaderGraphView.Pan += (worldAfter - worldBefore);
            }
        }

        DrawGrid(drawList, canvasPos, canvasSize, _shaderGraphView.Pan, _shaderGraphView.Zoom);

        var edges = graph.BuildEdges().ToArray();
        DrawShaderGraphLinks(drawList, edges, canvasPos);
        DrawShaderGraphNodes(drawList, graph, canvasPos);

        ImGui.EndChild();
    }

    private static void DrawShaderGraphNodes(ImDrawListPtr drawList, ShaderGraph graph, Vector2 canvasPos)
    {
        const float nodeW = 240f;
        const float nodeH = 86f;
        const float rounding = 8f;

        uint nodeBg = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint nodeBorder = ImGui.GetColorU32(ImGuiCol.Border);
        uint selectedBorder = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);

        foreach (var node in graph.Nodes.OrderBy(n => n.Kind).ThenBy(n => n.Name, StringComparer.Ordinal))
        {
            if (!_shaderGraphView.NodePositions.TryGetValue(node.Id, out var world))
                world = Vector2.Zero;

            Vector2 pMin = WorldToScreen(canvasPos, _shaderGraphView, world);
            Vector2 pMax = pMin + new Vector2(nodeW, nodeH) * _shaderGraphView.Zoom;

            bool isSelected = _shaderGraphView.SelectedNodeId == node.Id;
            uint border = isSelected ? selectedBorder : nodeBorder;

            drawList.AddRectFilled(pMin, pMax, nodeBg, rounding * _shaderGraphView.Zoom);
            drawList.AddRect(pMin, pMax, border, rounding * _shaderGraphView.Zoom, ImDrawFlags.None, 2.0f);

            ImGui.SetCursorScreenPos(pMin);
            ImGui.InvisibleButton($"##ShaderGraphNode{node.Id}", new Vector2(nodeW, nodeH) * _shaderGraphView.Zoom);

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _shaderGraphView.SelectedNodeId = node.Id;

            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
                _shaderGraphView.NodePositions[node.Id] = world + (ImGui.GetIO().MouseDelta / MathF.Max(1e-6f, _shaderGraphView.Zoom));

            float textPad = 8f * _shaderGraphView.Zoom;
            string title = node.Kind switch
            {
                ShaderGraphNodeKind.Attribute => $"Attribute: {node.OutputName}",
                ShaderGraphNodeKind.Uniform => $"Uniform: {node.OutputName}",
                ShaderGraphNodeKind.Constant => $"Const: {node.OutputName}",
                ShaderGraphNodeKind.Output => $"Out: {node.OutputName}",
                ShaderGraphNodeKind.MethodDefinition => $"Method: {node.MethodName}",
                _ => $"{node.MethodName ?? node.Name} -> {node.OutputName}"
            };
            drawList.AddText(pMin + new Vector2(textPad, 6f * _shaderGraphView.Zoom), ImGui.GetColorU32(ImGuiCol.Text), title);

            if (node.Kind == ShaderGraphNodeKind.MethodInvocation)
            {
                string inputs = node.Inputs.Count == 0 ? "Inputs: none" : $"Inputs: {string.Join(", ", node.Inputs.Select(i => i.SourceVariable ?? i.Name))}";
                drawList.AddText(pMin + new Vector2(textPad, 38f * _shaderGraphView.Zoom), ImGui.GetColorU32(ImGuiCol.TextDisabled), inputs);
            }
        }
    }

    private static void DrawShaderGraphLinks(ImDrawListPtr drawList, IReadOnlyCollection<ShaderGraphEdge> edges, Vector2 canvasPos)
    {
        const float nodeW = 240f;
        const float nodeH = 86f;

        uint linkColor = ImGui.GetColorU32(ImGuiCol.PlotLines);

        var nodeLookup = _activeShaderGraph?.Nodes.ToDictionary(n => n.Id);
        if (nodeLookup is null)
            return;

        foreach (var edge in edges)
        {
            if (!nodeLookup.TryGetValue(edge.FromId, out var fromNode) || !nodeLookup.TryGetValue(edge.ToId, out var toNode))
                continue;

            if (!_shaderGraphView.NodePositions.TryGetValue(edge.FromId, out var fromPos) ||
                !_shaderGraphView.NodePositions.TryGetValue(edge.ToId, out var toPos))
                continue;

            Vector2 fromAnchor = WorldToScreen(canvasPos, _shaderGraphView, fromPos) + new Vector2(nodeW * 0.5f * _shaderGraphView.Zoom, nodeH * _shaderGraphView.Zoom);
            Vector2 toAnchor = WorldToScreen(canvasPos, _shaderGraphView, toPos) + new Vector2(nodeW * 0.5f * _shaderGraphView.Zoom, 0);

            float dy = MathF.Max(40f, (toAnchor.Y - fromAnchor.Y) * 0.5f);
            Vector2 c1 = fromAnchor + new Vector2(0, dy);
            Vector2 c2 = toAnchor - new Vector2(0, dy);

            drawList.AddBezierCubic(fromAnchor, c1, c2, toAnchor, linkColor, 2.0f);
        }
    }

    private static void DrawShaderGraphInspector()
    {
        if (_activeShaderGraph is null)
        {
            ImGui.TextDisabled("No shader graph loaded.");
            return;
        }

        if (!_shaderGraphView.SelectedNodeId.HasValue)
        {
            ImGui.TextDisabled("Select a node to edit it.");
            return;
        }

        var node = _activeShaderGraph.FindNode(_shaderGraphView.SelectedNodeId.Value);
        if (node is null)
        {
            ImGui.TextDisabled("Node not found.");
            return;
        }

        ImGui.Text($"Node #{node.Id}: {node.Name}");
        ImGui.Text($"Kind: {node.Kind}");

        if (node.Kind != ShaderGraphNodeKind.MethodInvocation)
        {
            ImGui.TextDisabled("Only method invocation nodes are editable.");
            return;
        }

        string outputName = node.OutputName ?? "";
        if (ImGui.InputText("Output", ref outputName, (uint)256))
            node.OutputName = outputName;

        foreach (var input in node.Inputs)
        {
            string current = input.SourceVariable ?? "<unbound>";
            string preview = string.IsNullOrWhiteSpace(current) ? "<unbound>" : current;
            if (ImGui.BeginCombo($"Input: {input.Name}", preview))
            {
                foreach (var option in _activeShaderGraph.GetAvailableValueNames(node))
                {
                    bool selected = string.Equals(option, input.SourceVariable, StringComparison.Ordinal);
                    if (ImGui.Selectable(option, selected))
                        input.SourceVariable = option;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                if (ImGui.Selectable("<unbound>", string.IsNullOrWhiteSpace(input.SourceVariable)))
                    input.SourceVariable = null;
                ImGui.EndCombo();
            }
        }
    }

    private static void TryBuildShaderGraphFromSource()
    {
        try
        {
            _activeShaderGraph = ShaderGraph.FromGlsl(_shaderGraphSource);
            _shaderGraphError = "";
            _shaderGraphGenerated = "";
            _shaderGraphView.NodePositions.Clear();
            _shaderGraphView.SelectedNodeId = null;
            AutoLayoutShaderGraph();
        }
        catch (Exception ex)
        {
            _shaderGraphError = ex.Message;
        }
    }

    private static void TryGenerateShaderFromGraph()
    {
        if (_activeShaderGraph is null)
            return;

        try
        {
            var generator = new ShaderGraphGenerator(_activeShaderGraph);
            _shaderGraphGenerated = generator.Generate();
            _shaderGraphError = "";
        }
        catch (Exception ex)
        {
            _shaderGraphError = ex.Message;
        }
    }

    private static void AutoLayoutShaderGraph()
    {
        if (_activeShaderGraph is null)
            return;
        AutoLayoutShaderGraph(_activeShaderGraph, _shaderGraphView);
        _shaderGraphView.HasLayout = true;
    }

    private static void AutoLayoutShaderGraph(ShaderGraph graph, ShaderGraphViewState view)
    {
        view.NodePositions.Clear();

        const float columnSpacing = 320f;
        const float rowSpacing = 130f;

        int currentRow = 0;
        void PlaceNodes(IEnumerable<ShaderGraphNode> nodes, int column)
        {
            foreach (var node in nodes)
            {
                view.NodePositions[node.Id] = new Vector2(column * columnSpacing, currentRow * rowSpacing);
                currentRow++;
            }
        }

        PlaceNodes(graph.Nodes.Where(n => n.Kind is ShaderGraphNodeKind.Attribute or ShaderGraphNodeKind.Uniform or ShaderGraphNodeKind.Constant).OrderBy(n => n.Name, StringComparer.Ordinal), 0);

        currentRow = 0;
        PlaceNodes(graph.Nodes.Where(n => n.Kind == ShaderGraphNodeKind.MethodDefinition).OrderBy(n => n.Name, StringComparer.Ordinal), 1);

        currentRow = 0;
        PlaceNodes(graph.Nodes.Where(n => n.Kind == ShaderGraphNodeKind.MethodInvocation).OrderBy(n => n.Name, StringComparer.Ordinal), 2);

        currentRow = 0;
        PlaceNodes(graph.Nodes.Where(n => n.Kind == ShaderGraphNodeKind.Output).OrderBy(n => n.Name, StringComparer.Ordinal), 3);

        if (view.SelectedNodeId.HasValue && !view.NodePositions.ContainsKey(view.SelectedNodeId.Value))
            view.SelectedNodeId = null;
    }

    private static Vector2 WorldToScreen(Vector2 canvasPos, ShaderGraphViewState view, Vector2 world)
        => canvasPos + (world + view.Pan) * view.Zoom;
}
