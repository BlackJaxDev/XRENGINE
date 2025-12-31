using ImGuiNET;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static WeakReference<RenderPipeline>? _renderPipelineGraphPinnedPipeline;

    public static void OpenRenderPipelineGraph(RenderPipeline pipeline)
    {
        if (pipeline is null)
            return;

        _renderPipelineGraphPinnedPipeline = new WeakReference<RenderPipeline>(pipeline);
        _showRenderPipelineGraph = true;
    }

    private sealed class RenderPipelineGraphViewState
    {
        public Vector2 Pan = new(40, 40);
        public float Zoom = 1.0f;
        public int? SelectedPassIndex;
        public readonly Dictionary<int, Vector2> NodePositions = new();
        public bool HasLayout;
    }

    private sealed class RenderPipelineCommandGraphViewState
    {
        public Vector2 Pan = new(40, 40);
        public float Zoom = 1.0f;
        public int? SelectedNodeId;
        public readonly Dictionary<int, Vector2> NodePositions = new();
        public bool HasLayout;
    }

    private sealed class GraphNodeId
    {
        public GraphNodeId(int id) => Id = id;
        public int Id { get; }
    }

    private static readonly ConditionalWeakTable<object, GraphNodeId> _graphNodeIds = new();
    private static int _nextGraphNodeId;

    private static readonly Dictionary<Guid, RenderPipelineGraphViewState> _renderPipelineGraphStates = new();
    private static readonly Dictionary<Guid, RenderPipelineCommandGraphViewState> _renderPipelineCommandGraphStates = new();

    private static void DrawRenderPipelineGraphPanel()
    {
        if (!_showRenderPipelineGraph)
            return;

        ImGui.SetNextWindowSize(new Vector2(900, 700), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Render Pipeline Graph", ref _showRenderPipelineGraph, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        var pipeline = TryGetPinnedOrActiveRenderPipeline();
        if (pipeline is null)
        {
            ImGui.TextDisabled("No active render pipeline found (no viewport/camera pipeline currently available).");
            ImGui.End();
            return;
        }

        var passMetadata = pipeline.PassMetadata ?? Array.Empty<RenderPassMetadata>();
        var passState = GetOrCreatePassGraphState(pipeline.ID);
        var commandState = GetOrCreateCommandGraphState(pipeline.ID);

        ImGui.Text($"Pipeline: {pipeline.DebugName}");

        ImGui.Separator();

        if (ImGui.BeginTabBar("##RenderPipelineGraphTabs"))
        {
            if (ImGui.BeginTabItem("Commands"))
            {
                DrawCommandGraphToolbar(pipeline, commandState);
                DrawRenderPipelineCommandGraphCanvas(pipeline, commandState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Passes"))
            {
                if (!passState.HasLayout || !passState.NodePositions.Keys.ToHashSet().SetEquals(passMetadata.Select(p => p.PassIndex)))
                {
                    AutoLayout(passMetadata, passState);
                    passState.HasLayout = true;
                }

                if (ImGui.BeginMenuBar())
                {
                    if (ImGui.MenuItem("Auto Layout"))
                        AutoLayout(passMetadata, passState);

                    if (ImGui.MenuItem("Reset View"))
                    {
                        passState.Pan = new Vector2(40, 40);
                        passState.Zoom = 1.0f;
                    }

                    ImGui.EndMenuBar();
                }

                ImGui.TextDisabled($"Passes: {passMetadata.Count}");
                ImGui.Separator();
                DrawRenderPipelinePassGraphCanvas(passMetadata, passState);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private static RenderPipelineGraphViewState GetOrCreateGraphState(Guid pipelineId)
    {
        if (_renderPipelineGraphStates.TryGetValue(pipelineId, out var existing))
            return existing;

        var created = new RenderPipelineGraphViewState();
        _renderPipelineGraphStates[pipelineId] = created;
        return created;
    }

    private static RenderPipelineGraphViewState GetOrCreatePassGraphState(Guid pipelineId)
        => GetOrCreateGraphState(pipelineId);

    private static RenderPipelineCommandGraphViewState GetOrCreateCommandGraphState(Guid pipelineId)
    {
        if (_renderPipelineCommandGraphStates.TryGetValue(pipelineId, out var existing))
            return existing;

        var created = new RenderPipelineCommandGraphViewState();
        _renderPipelineCommandGraphStates[pipelineId] = created;
        return created;
    }

    private static RenderPipeline? TryGetActiveRenderPipeline()
    {
        foreach (var window in Engine.Windows)
        {
            if (window is null)
                continue;

            foreach (var viewport in window.Viewports)
            {
                var pipeline = viewport?.RenderPipeline;
                if (pipeline is not null)
                    return pipeline;

                var cameraPipeline = viewport?.ActiveCamera?.RenderPipeline;
                if (cameraPipeline is not null)
                    return cameraPipeline;
            }
        }

        return null;
    }

    private static RenderPipeline? TryGetPinnedOrActiveRenderPipeline()
    {
        if (_renderPipelineGraphPinnedPipeline is not null &&
            _renderPipelineGraphPinnedPipeline.TryGetTarget(out var pinned) &&
            pinned is not null &&
            !pinned.IsDestroyed)
        {
            return pinned;
        }

        return TryGetActiveRenderPipeline();
    }

    private static int GetStableGraphNodeId(object obj)
    {
        var holder = _graphNodeIds.GetValue(obj, static _ => new GraphNodeId(Interlocked.Increment(ref _nextGraphNodeId)));
        return holder.Id;
    }

    private readonly record struct CommandGraphNode(int Id, string Label, bool IsContainer, object BackingObject);
    private readonly record struct CommandGraphEdge(int FromId, int ToId, string? Label);

    private static void DrawCommandGraphToolbar(RenderPipeline pipeline, RenderPipelineCommandGraphViewState state)
    {
        if (ImGui.BeginMenuBar())
        {
            if (ImGui.MenuItem("Auto Layout"))
            {
                AutoLayoutCommandGraph(pipeline, state);
                state.HasLayout = true;
            }

            if (ImGui.MenuItem("Reset View"))
            {
                state.Pan = new Vector2(40, 40);
                state.Zoom = 1.0f;
            }

            if (_renderPipelineGraphPinnedPipeline is not null && _renderPipelineGraphPinnedPipeline.TryGetTarget(out var pinned) && pinned is not null && ReferenceEquals(pinned, pipeline))
            {
                if (ImGui.MenuItem("Unpin"))
                    _renderPipelineGraphPinnedPipeline = null;
            }

            ImGui.EndMenuBar();
        }

        int commandCount = pipeline.CommandChain?.Count ?? 0;
        ImGui.TextDisabled($"Commands: {commandCount}");
        ImGui.SameLine();
        ImGui.TextDisabled("| Pan: RMB/MMB drag or Space+LMB | Zoom: Wheel (Ctrl=fast) | Drag nodes: LMB");
        ImGui.Separator();
    }

    private static void DrawRenderPipelineCommandGraphCanvas(RenderPipeline pipeline, RenderPipelineCommandGraphViewState view)
    {
        var root = pipeline.CommandChain;
        if (root is null)
        {
            ImGui.TextDisabled("Pipeline has no CommandChain.");
            return;
        }

        BuildCommandGraph(root, out var nodes, out var edges);

        var containerColumnWidths = ComputeCommandContainerColumnWidths(root);

        if (!view.HasLayout || !view.NodePositions.Keys.ToHashSet().SetEquals(nodes.Select(n => n.Id)))
        {
            AutoLayoutCommandGraph(root, view, containerColumnWidths);
            view.HasLayout = true;
        }

        DrawCommandGraphCanvas(nodes, edges, view, containerColumnWidths);
    }

    private static Dictionary<int, int> ComputeCommandContainerColumnWidths(ViewportRenderCommandContainer root)
    {
        var widths = new Dictionary<int, int>();
        var seen = new HashSet<ViewportRenderCommandContainer>(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        int Measure(ViewportRenderCommandContainer container)
        {
            if (!seen.Add(container))
                return 1;

            int containerId = GetStableGraphNodeId(container);
            int maxWidth = 1;

            foreach (var cmd in container)
            {
                int branchWidthSum = 0;
                foreach (var (_, childContainer) in EnumerateChildContainers(cmd).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (childContainer is null)
                        continue;
                    branchWidthSum += Measure(childContainer);
                }

                if (branchWidthSum > 0)
                    maxWidth = Math.Max(maxWidth, branchWidthSum);
            }

            widths[containerId] = maxWidth;
            return maxWidth;
        }

        _ = Measure(root);
        return widths;
    }

    private static void BuildCommandGraph(
        ViewportRenderCommandContainer root,
        out ReadOnlyCollection<CommandGraphNode> nodes,
        out ReadOnlyCollection<CommandGraphEdge> edges)
    {
        var nodeList = new List<CommandGraphNode>(256);
        var edgeList = new List<CommandGraphEdge>(512);
        var seenContainers = new HashSet<ViewportRenderCommandContainer>(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        void AddContainer(ViewportRenderCommandContainer container, string label, int? parentCommandNodeId, string? edgeLabel)
        {
            if (!seenContainers.Add(container))
                return;

            int containerId = GetStableGraphNodeId(container);
            nodeList.Add(new CommandGraphNode(containerId, label, IsContainer: true, container));

            if (parentCommandNodeId.HasValue)
                edgeList.Add(new CommandGraphEdge(parentCommandNodeId.Value, containerId, edgeLabel));

            int? prev = null;
            foreach (var cmd in container)
            {
                int cmdId = GetStableGraphNodeId(cmd);
                nodeList.Add(new CommandGraphNode(cmdId, cmd.GetType().Name, IsContainer: false, cmd));

                if (prev is null)
                    edgeList.Add(new CommandGraphEdge(containerId, cmdId, null));
                else
                    edgeList.Add(new CommandGraphEdge(prev.Value, cmdId, null));

                foreach (var (childName, childContainer) in EnumerateChildContainers(cmd))
                {
                    if (childContainer is null)
                        continue;
                    AddContainer(childContainer, childName, cmdId, childName);
                }

                prev = cmdId;
            }
        }

        AddContainer(root, "CommandChain", parentCommandNodeId: null, edgeLabel: null);

        nodes = new ReadOnlyCollection<CommandGraphNode>(nodeList
            .GroupBy(n => n.Id)
            .Select(g => g.First())
            .ToList());
        edges = new ReadOnlyCollection<CommandGraphEdge>(edgeList);
    }

    private static IEnumerable<(string Name, ViewportRenderCommandContainer? Container)> EnumerateChildContainers(ViewportRenderCommand cmd)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var t = cmd.GetType();

        foreach (var prop in t.GetProperties(flags))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                continue;

            if (prop.Name == nameof(ViewportRenderCommand.CommandContainer))
                continue;

            if (typeof(ViewportRenderCommandContainer).IsAssignableFrom(prop.PropertyType))
            {
                ViewportRenderCommandContainer? child = null;
                try { child = (ViewportRenderCommandContainer?)prop.GetValue(cmd); } catch { }

                // Avoid traversing back to the parent container.
                if (child is not null && cmd.CommandContainer is not null && ReferenceEquals(child, cmd.CommandContainer))
                    continue;

                yield return (prop.Name, child);
            }
        }
    }

    private static void AutoLayoutCommandGraph(RenderPipeline pipeline, RenderPipelineCommandGraphViewState view)
    {
        var root = pipeline.CommandChain;
        if (root is null)
            return;
        AutoLayoutCommandGraph(root, view);
    }

    private static void AutoLayoutCommandGraph(ViewportRenderCommandContainer root, RenderPipelineCommandGraphViewState view)
    {
        AutoLayoutCommandGraph(root, view, ComputeCommandContainerColumnWidths(root));
    }

    private static void AutoLayoutCommandGraph(ViewportRenderCommandContainer root, RenderPipelineCommandGraphViewState view, IReadOnlyDictionary<int, int> columnWidths)
    {
        view.NodePositions.Clear();

        var seenContainers = new HashSet<ViewportRenderCommandContainer>(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        const float xStep = 320f;
        const float yStep = 130f;
        const float containerToFirstCommandPadY = 60f;

        float LayoutContainer(ViewportRenderCommandContainer container, float colStartX, float y)
        {
            if (!seenContainers.Add(container))
                return y;

            int containerId = GetStableGraphNodeId(container);
            int colWidth = columnWidths.TryGetValue(containerId, out var w) ? w : 1;
            float spanWidthPx = colWidth * xStep;

            // Container nodes are header bars: position is their top-left.
            view.NodePositions[containerId] = new Vector2(colStartX, y);

            float cmdY = y + containerToFirstCommandPadY;

            foreach (var cmd in container)
            {
                int cmdId = GetStableGraphNodeId(cmd);

                // Center command node within the container span.
                const float cmdW = 240f;
                float cmdX = colStartX + (spanWidthPx - cmdW) * 0.5f;
                view.NodePositions[cmdId] = new Vector2(cmdX, cmdY);

                // Branches form columns next to each other, starting below the command.
                var children = EnumerateChildContainers(cmd)
                    .Where(x => x.Container is not null)
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ToList();

                if (children.Count > 0)
                {
                    int childTotalWidth = 0;
                    var childWidths = new List<int>(children.Count);
                    foreach (var (_, childContainer) in children)
                    {
                        int childId = GetStableGraphNodeId(childContainer!);
                        int cw = columnWidths.TryGetValue(childId, out var ww) ? ww : 1;
                        childWidths.Add(cw);
                        childTotalWidth += cw;
                    }

                    float childColStartX = colStartX + ((colWidth - childTotalWidth) * 0.5f * xStep);
                    float childYStart = cmdY + yStep;
                    float maxChildYEnd = childYStart;

                    // IMPORTANT: all children start at the same Y (side-by-side columns).
                    for (int i = 0; i < children.Count; i++)
                    {
                        var (_, childContainer) = children[i];
                        int cw = childWidths[i];
                        float yEnd = LayoutContainer(childContainer!, childColStartX, childYStart);
                        maxChildYEnd = Math.Max(maxChildYEnd, yEnd);
                        childColStartX += cw * xStep;
                    }

                    cmdY = maxChildYEnd + yStep;
                }
                else
                {
                    cmdY += yStep;
                }
            }

            return cmdY;
        }

        _ = LayoutContainer(root, colStartX: 0f, y: 0f);

        if (view.SelectedNodeId.HasValue && !view.NodePositions.ContainsKey(view.SelectedNodeId.Value))
            view.SelectedNodeId = null;
    }

    private static void DrawCommandGraphCanvas(
        ReadOnlyCollection<CommandGraphNode> nodes,
        ReadOnlyCollection<CommandGraphEdge> edges,
        RenderPipelineCommandGraphViewState view,
        IReadOnlyDictionary<int, int> containerColumnWidths)
    {
        var io = ImGui.GetIO();

        ImGui.BeginChild(
            "##RenderPipelineCommandGraphCanvas",
            new Vector2(0, 0),
            ImGuiChildFlags.Border,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();

        if (canvasSize.X < 1 || canvasSize.Y < 1)
        {
            ImGui.EndChild();
            return;
        }

        ImGui.InvisibleButton(
            "##RenderPipelineCommandGraphCanvasBtn",
            canvasSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);

        bool canvasHovered = ImGui.IsItemHovered();

        if (canvasHovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0f) || ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f) || (ImGui.IsKeyDown(ImGuiKey.Space) && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))))
            view.Pan += io.MouseDelta / MathF.Max(1e-6f, view.Zoom);

        if (canvasHovered && Math.Abs(io.MouseWheel) > 1e-6f)
        {
            float prevZoom = view.Zoom;
            float zoomBase = io.KeyCtrl ? 1.15f : 1.07f;
            float nextZoom = Math.Clamp(view.Zoom * (float)Math.Pow(zoomBase, io.MouseWheel), 0.25f, 2.5f);
            if (Math.Abs(nextZoom - prevZoom) > 1e-6f)
            {
                var mouse = io.MousePos;
                Vector2 mouseLocal = mouse - canvasPos;
                Vector2 worldBefore = (mouseLocal / prevZoom) - view.Pan;
                view.Zoom = nextZoom;
                Vector2 worldAfter = (mouseLocal / nextZoom) - view.Pan;
                view.Pan += (worldAfter - worldBefore);
            }
        }

        DrawGrid(drawList, canvasPos, canvasSize, new RenderPipelineGraphViewState { Pan = view.Pan, Zoom = view.Zoom });

        var nodeInfo = nodes.ToDictionary(n => n.Id, n => n.IsContainer);
        DrawCommandGraphLinks(drawList, edges, view, canvasPos, nodeInfo, containerColumnWidths);
        DrawCommandGraphNodes(drawList, nodes, view, canvasPos, containerColumnWidths);

        ImGui.EndChild();
    }

    private static void DrawCommandGraphLinks(
        ImDrawListPtr drawList,
        ReadOnlyCollection<CommandGraphEdge> edges,
        RenderPipelineCommandGraphViewState view,
        Vector2 canvasPos,
        IReadOnlyDictionary<int, bool> nodeIsContainer,
        IReadOnlyDictionary<int, int> containerColumnWidths)
    {
        const float cmdW = 240f;
        const float cmdH = 74f;
        const float containerH = 28f;
        const float xStep = 320f;

        uint linkColor = ImGui.GetColorU32(ImGuiCol.PlotLines);
        uint labelColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

        Vector2 AnchorTopBottom(int id, bool isContainer, bool bottom, float xT)
        {
            if (!view.NodePositions.TryGetValue(id, out var world))
                return Vector2.Zero;
            float w;
            if (isContainer)
            {
                int cols = containerColumnWidths.TryGetValue(id, out var cw) ? cw : 1;
                w = cols * xStep;
            }
            else
            {
                w = cmdW;
            }
            float h = (isContainer ? containerH : cmdH);
            Vector2 p = WorldToScreen(canvasPos, view, world);
            float xLocal = (w * xT) * view.Zoom;
            return bottom
                ? p + new Vector2(xLocal, h * view.Zoom)
                : p + new Vector2(xLocal, 0);
        }

        // Fan-out/fan-in anchors so multi-output nodes (if/else) don't overlap visually.
        var outIndex = new Dictionary<(int From, int To), int>();
        var outCounts = edges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Count());

        var outSeen = new Dictionary<int, int>();
        foreach (var e in edges)
        {
            if (!outSeen.TryGetValue(e.FromId, out int idx))
                idx = 0;
            outIndex[(e.FromId, e.ToId)] = idx;
            outSeen[e.FromId] = idx + 1;
        }

        foreach (var e in edges)
        {
            bool fromIsContainer = nodeIsContainer.TryGetValue(e.FromId, out var fic) && fic;
            bool toIsContainer = nodeIsContainer.TryGetValue(e.ToId, out var tic) && tic;

            int count = outCounts.TryGetValue(e.FromId, out int c) ? c : 1;
            int idx = outIndex.TryGetValue((e.FromId, e.ToId), out int oi) ? oi : 0;
            float xT = count <= 1 ? 0.5f : (0.2f + (0.6f * (idx / MathF.Max(1f, count - 1f))));

            Vector2 a = AnchorTopBottom(e.FromId, fromIsContainer, bottom: true, xT);
            Vector2 b = AnchorTopBottom(e.ToId, toIsContainer, bottom: false, 0.5f);
            if (a == Vector2.Zero || b == Vector2.Zero)
                continue;

            float dy = MathF.Max(40f, (b.Y - a.Y) * 0.5f);
            Vector2 c1 = a + new Vector2(0, dy);
            Vector2 c2 = b - new Vector2(0, dy);
            drawList.AddBezierCubic(a, c1, c2, b, linkColor, 2.0f);

            if (!string.IsNullOrWhiteSpace(e.Label))
            {
                Vector2 mid = (a + b) * 0.5f;
                drawList.AddText(mid + new Vector2(6f, -10f), labelColor, e.Label);
            }
        }
    }

    private static void DrawCommandGraphNodes(ImDrawListPtr drawList, ReadOnlyCollection<CommandGraphNode> nodes, RenderPipelineCommandGraphViewState view, Vector2 canvasPos, IReadOnlyDictionary<int, int> containerColumnWidths)
    {
        const float cmdW = 240f;
        const float cmdH = 74f;
        const float containerH = 28f;
        const float xStep = 320f;
        const float rounding = 8f;

        uint cmdBg = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint cmdBorder = ImGui.GetColorU32(ImGuiCol.Border);
        uint containerBg = ImGui.GetColorU32(ImGuiCol.TitleBg);
        uint containerBorder = ImGui.GetColorU32(ImGuiCol.Border);
        uint selectedBorder = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);

        foreach (var node in nodes.OrderBy(n => n.IsContainer ? 0 : 1).ThenBy(n => n.Label, StringComparer.Ordinal))
        {
            if (!view.NodePositions.TryGetValue(node.Id, out var world))
                continue;

            float w;
            if (node.IsContainer)
            {
                int cols = containerColumnWidths.TryGetValue(node.Id, out var cw) ? cw : 1;
                w = cols * xStep;
            }
            else
            {
                w = cmdW;
            }
            float h = node.IsContainer ? containerH : cmdH;
            Vector2 pMin = WorldToScreen(canvasPos, view, world);
            Vector2 pMax = pMin + new Vector2(w, h) * view.Zoom;

            bool isSelected = view.SelectedNodeId == node.Id;
            uint border = isSelected ? selectedBorder : (node.IsContainer ? containerBorder : cmdBorder);
            uint bg = node.IsContainer ? containerBg : cmdBg;

            // Container nodes are header bars.
            float corner = node.IsContainer ? 0f : (rounding * view.Zoom);
            drawList.AddRectFilled(pMin, pMax, bg, corner);
            drawList.AddRect(pMin, pMax, border, corner, ImDrawFlags.None, 2.0f);

            ImGui.SetCursorScreenPos(pMin);
            ImGui.InvisibleButton($"##RPCmdNode{node.Id}", new Vector2(w, h) * view.Zoom);

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                view.SelectedNodeId = node.Id;

            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
                view.NodePositions[node.Id] = world + (ImGui.GetIO().MouseDelta / MathF.Max(1e-6f, view.Zoom));

            float textPad = 8f * view.Zoom;
            drawList.AddText(pMin + new Vector2(textPad, 4f * view.Zoom), ImGui.GetColorU32(ImGuiCol.Text), node.Label);

            if (!node.IsContainer)
            {
                if (node.BackingObject is ViewportRenderCommand cmd && cmd.CommandContainer is not null)
                {
                    string sub = cmd.CommandContainer.BranchResources.ToString();
                    drawList.AddText(pMin + new Vector2(textPad, 34f * view.Zoom), ImGui.GetColorU32(ImGuiCol.TextDisabled), sub);
                }
            }
        }
    }

    private static Vector2 WorldToScreen(Vector2 canvasPos, RenderPipelineCommandGraphViewState view, Vector2 world)
        => canvasPos + (world + view.Pan) * view.Zoom;

    private static void DrawRenderPipelinePassGraphCanvas(IReadOnlyCollection<RenderPassMetadata> passes, RenderPipelineGraphViewState view)
    {
        var io = ImGui.GetIO();

        ImGui.BeginChild(
            "##RenderPipelineGraphCanvas",
            new Vector2(0, 0),
            ImGuiChildFlags.Border,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();

        if (canvasSize.X < 1 || canvasSize.Y < 1)
        {
            ImGui.EndChild();
            return;
        }

        // Canvas interaction surface.
        ImGui.InvisibleButton("##RenderPipelineGraphCanvasBtn", canvasSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);

        bool canvasHovered = ImGui.IsItemHovered();

        // Pan (RMB/MMB drag, or Space+LMB)
        if (canvasHovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0f) || ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f) || (ImGui.IsKeyDown(ImGuiKey.Space) && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))))
        {
            view.Pan += io.MouseDelta / MathF.Max(1e-6f, view.Zoom);
        }

        // Zoom (wheel), centered on mouse (Ctrl zooms faster)
        if (canvasHovered && Math.Abs(io.MouseWheel) > 1e-6f)
        {
            float prevZoom = view.Zoom;
            float zoomBase = io.KeyCtrl ? 1.15f : 1.07f;
            float nextZoom = Math.Clamp(view.Zoom * (float)Math.Pow(zoomBase, io.MouseWheel), 0.25f, 2.5f);
            if (Math.Abs(nextZoom - prevZoom) > 1e-6f)
            {
                var mouse = io.MousePos;
                Vector2 mouseLocal = mouse - canvasPos;
                Vector2 worldBefore = (mouseLocal / prevZoom) - view.Pan;
                view.Zoom = nextZoom;
                Vector2 worldAfter = (mouseLocal / nextZoom) - view.Pan;
                view.Pan += (worldAfter - worldBefore);
            }
        }

        DrawGrid(drawList, canvasPos, canvasSize, view);

        // Links first (behind nodes)
        DrawLinks(drawList, passes, view, canvasPos);

        // Nodes
        DrawNodes(drawList, passes, view, canvasPos);

        ImGui.EndChild();
    }

    private static void DrawGrid(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, RenderPipelineGraphViewState view)
    {
        float gridStep = 64.0f * view.Zoom;
        if (gridStep < 10f)
            gridStep = 10f;

        uint gridColor = ImGui.GetColorU32(ImGuiCol.Separator);
        uint originColor = ImGui.GetColorU32(ImGuiCol.PlotLines);

        Vector2 origin = canvasPos + (view.Pan * view.Zoom);

        float startX = Mod(origin.X, gridStep);
        float startY = Mod(origin.Y, gridStep);

        for (float x = startX; x < canvasSize.X; x += gridStep)
            drawList.AddLine(new Vector2(canvasPos.X + x, canvasPos.Y), new Vector2(canvasPos.X + x, canvasPos.Y + canvasSize.Y), gridColor);

        for (float y = startY; y < canvasSize.Y; y += gridStep)
            drawList.AddLine(new Vector2(canvasPos.X, canvasPos.Y + y), new Vector2(canvasPos.X + canvasSize.X, canvasPos.Y + y), gridColor);

        // Axes
        drawList.AddLine(new Vector2(origin.X, canvasPos.Y), new Vector2(origin.X, canvasPos.Y + canvasSize.Y), originColor, 2.0f);
        drawList.AddLine(new Vector2(canvasPos.X, origin.Y), new Vector2(canvasPos.X + canvasSize.X, origin.Y), originColor, 2.0f);
    }

    private static void DrawLinks(ImDrawListPtr drawList, IReadOnlyCollection<RenderPassMetadata> passes, RenderPipelineGraphViewState view, Vector2 canvasPos)
    {
        const float nodeW = 220f;
        const float nodeH = 76f;

        uint linkColor = ImGui.GetColorU32(ImGuiCol.PlotLines);

        foreach (var pass in passes)
        {
            if (pass.ExplicitDependencies is null || pass.ExplicitDependencies.Count == 0)
                continue;

            if (!view.NodePositions.TryGetValue(pass.PassIndex, out var passPos))
                continue;

            Vector2 passCenterIn = WorldToScreen(canvasPos, view, passPos + new Vector2(0, nodeH * 0.5f));

            foreach (int dep in pass.ExplicitDependencies)
            {
                if (!view.NodePositions.TryGetValue(dep, out var depPos))
                    continue;

                Vector2 depCenterOut = WorldToScreen(canvasPos, view, depPos + new Vector2(nodeW, nodeH * 0.5f));

                float dx = MathF.Max(40f, (passCenterIn.X - depCenterOut.X) * 0.5f);
                Vector2 c1 = depCenterOut + new Vector2(dx, 0);
                Vector2 c2 = passCenterIn - new Vector2(dx, 0);

                drawList.AddBezierCubic(depCenterOut, c1, c2, passCenterIn, linkColor, 2.0f);
            }
        }
    }

    private static void DrawNodes(ImDrawListPtr drawList, IReadOnlyCollection<RenderPassMetadata> passes, RenderPipelineGraphViewState view, Vector2 canvasPos)
    {
        const float nodeW = 220f;
        const float nodeH = 76f;
        const float rounding = 8f;

        uint nodeBg = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint nodeBorder = ImGui.GetColorU32(ImGuiCol.Border);
        uint nodeTitle = ImGui.GetColorU32(ImGuiCol.TitleBgActive);
        uint selectedBorder = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);

        foreach (var pass in passes.OrderBy(p => p.PassIndex))
        {
            if (!view.NodePositions.TryGetValue(pass.PassIndex, out var pos))
                continue;

            Vector2 pMin = WorldToScreen(canvasPos, view, pos);
            Vector2 pMax = pMin + new Vector2(nodeW, nodeH) * view.Zoom;

            bool isSelected = view.SelectedPassIndex == pass.PassIndex;
            uint border = isSelected ? selectedBorder : nodeBorder;

            drawList.AddRectFilled(pMin, pMax, nodeBg, rounding * view.Zoom);
            drawList.AddRect(pMin, pMax, border, rounding * view.Zoom, ImDrawFlags.None, 2.0f);

            // Header strip
            Vector2 headerMax = new(pMax.X, pMin.Y + 26f * view.Zoom);
            drawList.AddRectFilled(pMin, headerMax, nodeTitle, rounding * view.Zoom, ImDrawFlags.RoundCornersTop);

            // Interaction
            ImGui.SetCursorScreenPos(pMin);
            ImGui.InvisibleButton($"##RPGraphNode{pass.PassIndex}", new Vector2(nodeW, nodeH) * view.Zoom);

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                view.SelectedPassIndex = pass.PassIndex;

            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
            {
                view.NodePositions[pass.PassIndex] = pos + (ImGui.GetIO().MouseDelta / MathF.Max(1e-6f, view.Zoom));
            }

            // Text
            float textPad = 8f * view.Zoom;
            Vector2 titlePos = pMin + new Vector2(textPad, 5f * view.Zoom);
            drawList.AddText(titlePos, ImGui.GetColorU32(ImGuiCol.Text), pass.Name);

            string sub = $"Index: {pass.PassIndex}  Stage: {pass.Stage}";
            Vector2 subPos = pMin + new Vector2(textPad, 34f * view.Zoom);
            drawList.AddText(subPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), sub);

            int depCount = pass.ExplicitDependencies?.Count ?? 0;
            string deps = depCount == 0 ? "Deps: none" : $"Deps: {depCount}";
            Vector2 depPos = pMin + new Vector2(textPad, 54f * view.Zoom);
            drawList.AddText(depPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), deps);
        }
    }

    private static Vector2 WorldToScreen(Vector2 canvasPos, RenderPipelineGraphViewState view, Vector2 world)
        => canvasPos + (world + view.Pan) * view.Zoom;

    private static float Mod(float x, float m)
    {
        float r = x % m;
        return r < 0 ? r + m : r;
    }

    private static void AutoLayout(IReadOnlyCollection<RenderPassMetadata> passes, RenderPipelineGraphViewState view)
    {
        view.NodePositions.Clear();

        // Simple level (depth) assignment using explicit dependencies.
        var passById = passes.ToDictionary(p => p.PassIndex);
        var memoDepth = new Dictionary<int, int>();

        int DepthOf(int passIndex)
        {
            if (memoDepth.TryGetValue(passIndex, out int d))
                return d;

            if (!passById.TryGetValue(passIndex, out var pass))
                return memoDepth[passIndex] = 0;

            int maxDep = 0;
            if (pass.ExplicitDependencies is not null)
            {
                foreach (var dep in pass.ExplicitDependencies)
                    maxDep = Math.Max(maxDep, DepthOf(dep) + 1);
            }

            memoDepth[passIndex] = maxDep;
            return maxDep;
        }

        var groups = passes
            .GroupBy(p => DepthOf(p.PassIndex))
            .OrderBy(g => g.Key)
            .ToList();

        const float xStep = 300f;
        const float yStep = 110f;

        foreach (var group in groups)
        {
            int i = 0;
            foreach (var pass in group.OrderBy(p => p.PassIndex))
            {
                view.NodePositions[pass.PassIndex] = new Vector2(group.Key * xStep, i * yStep);
                i++;
            }
        }

        if (view.SelectedPassIndex.HasValue && !view.NodePositions.ContainsKey(view.SelectedPassIndex.Value))
            view.SelectedPassIndex = null;
    }
}
