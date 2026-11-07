using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const float WorkbenchOutlineWidth = 300f;
    private const float WorkbenchInspectorWidth = 340f;
    private const float WorkbenchPreviewHeight = 230f;
    private const long WorkbenchFollowRefreshIntervalMs = 500;

    private static readonly RenderPipelineWorkbenchWindowState _workbench = new();
    private static RenderPipeline? _workbenchFollowedPipeline;
    private static long _workbenchFollowRefreshTimestamp;
    private static bool _workbenchFocusRequested;

    /// <summary>
    /// Opens the Render Pipeline Workbench on <paramref name="pipeline"/> and, when given, on one of its instances.
    /// </summary>
    public static void OpenRenderPipelineWorkbench(RenderPipeline pipeline, XRRenderPipelineInstance? instance = null)
    {
        if (pipeline is null)
            return;

        _workbench.Pin(pipeline, instance);
        _showRenderPipelineWorkbench = true;
        _workbenchFocusRequested = true;
    }

    /// <summary>
    /// Opens the Render Pipeline Workbench. This name remains for one release for callers of the old graph panel.
    /// </summary>
    [Obsolete("Use OpenRenderPipelineWorkbench. The graph panel is now the Render Pipeline Workbench.")]
    public static void OpenRenderPipelineGraph(RenderPipeline pipeline)
        => OpenRenderPipelineWorkbench(pipeline);

    private const string RenderPipelineWorkbenchWindowName = "Render Pipeline Workbench";

    private static void DrawRenderPipelineWorkbenchPanel()
    {
        if (!_showRenderPipelineWorkbench)
            return;

        ImGui.SetNextWindowSize(new Vector2(1280f, 780f), ImGuiCond.FirstUseEver);
        if (_workbenchFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            _workbenchFocusRequested = false;
        }

        if (!ImGui.Begin(RenderPipelineWorkbenchWindowName, ref _showRenderPipelineWorkbench, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        DrawWorkbenchMenuBar();

        RenderPipeline? pipeline = ResolveWorkbenchPipeline();
        if (pipeline is null)
        {
            ImGui.TextDisabled("No render pipeline is active. Open the Workbench from a camera, a viewport, or a pipeline asset.");
            ImGui.End();
            return;
        }

        XRRenderPipelineInstance? instance = ResolveWorkbenchInstance(pipeline);
        _workbench.Model.Refresh(pipeline, instance);
        _workbench.Links.Update(_workbench.Model, _workbench.Selection.Current);
        _workbench.Execution.Update(_workbench.Model);
        _workbench.Timing.Update(_workbench.Model);

        DrawWorkbenchTargetBar(pipeline, instance);
        ImGui.Separator();

        Vector2 available = ImGui.GetContentRegionAvail();
        float previewHeight = _workbench.ShowPreview ? MathF.Min(WorkbenchPreviewHeight, available.Y * 0.4f) : 0f;
        float bodyHeight = MathF.Max(120f, available.Y - previewHeight);

        ImGui.BeginChild("##WorkbenchBody", new Vector2(0f, bodyHeight), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
        if (_workbench.ShowOutline)
        {
            ImGui.BeginChild("##WorkbenchOutline", new Vector2(WorkbenchOutlineWidth, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX);
            DrawWorkbenchOutline();
            ImGui.EndChild();
            ImGui.SameLine();
        }

        float centerWidth = _workbench.ShowInspector
            ? MathF.Max(200f, ImGui.GetContentRegionAvail().X - WorkbenchInspectorWidth - ImGui.GetStyle().ItemSpacing.X)
            : 0f;
        ImGui.BeginChild("##WorkbenchCenter", new Vector2(centerWidth, 0f), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
        DrawWorkbenchViews();
        ImGui.EndChild();

        if (_workbench.ShowInspector)
        {
            ImGui.SameLine();
            ImGui.BeginChild("##WorkbenchInspector", Vector2.Zero, ImGuiChildFlags.Borders);
            DrawWorkbenchInspector();
            ImGui.EndChild();
        }

        ImGui.EndChild();

        if (_workbench.ShowPreview)
        {
            ImGui.BeginChild("##WorkbenchPreview", Vector2.Zero, ImGuiChildFlags.Borders);
            DrawWorkbenchPreview();
            ImGui.EndChild();
        }

        ImGui.End();
    }

    private static void DrawWorkbenchMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.BeginMenu("View"))
        {
            bool showOutline = _workbench.ShowOutline;
            if (ImGui.MenuItem("Outline", null, ref showOutline))
                _workbench.ShowOutline = showOutline;

            bool showInspector = _workbench.ShowInspector;
            if (ImGui.MenuItem("Inspector", null, ref showInspector))
                _workbench.ShowInspector = showInspector;

            bool showPreview = _workbench.ShowPreview;
            if (ImGui.MenuItem("Preview", null, ref showPreview))
                _workbench.ShowPreview = showPreview;

            ImGui.Separator();
            if (ImGui.MenuItem("Reset Flow View"))
                _workbench.FlowView.Reset();
            if (ImGui.MenuItem("Reset Passes View"))
                _workbench.PassesView.Reset();
            ImGui.EndMenu();
        }

        if (_workbench.PinnedPipeline is not null && ImGui.MenuItem("Unpin"))
            _workbench.Unpin();

        ImGui.EndMenuBar();
    }

    private static void DrawWorkbenchViews()
    {
        if (!ImGui.BeginTabBar("##WorkbenchViews"))
            return;

        DrawWorkbenchViewTab(ERenderPipelineWorkbenchView.Flow, "Flow");
        DrawWorkbenchViewTab(ERenderPipelineWorkbenchView.Passes, "Passes");
        DrawWorkbenchViewTab(ERenderPipelineWorkbenchView.Resources, "Resources");
        DrawWorkbenchViewTab(ERenderPipelineWorkbenchView.Execution, "Execution");
        DrawWorkbenchViewTab(ERenderPipelineWorkbenchView.Settings, "Settings");
        DrawWorkbenchViewTab(ERenderPipelineWorkbenchView.Validation, "Validation");
        DrawWorkbenchViewTab(ERenderPipelineWorkbenchView.Script, "Script");
        _workbench.RequestedView = null;
        ImGui.EndTabBar();
    }

    private static void DrawWorkbenchViewTab(ERenderPipelineWorkbenchView view, string label)
    {
        bool visible;
        if (_workbench.RequestedView == view)
        {
            bool open = true;
            visible = ImGui.BeginTabItem(label, ref open, ImGuiTabItemFlags.SetSelected);
        }
        else
        {
            visible = ImGui.BeginTabItem(label);
        }

        if (!visible)
            return;

        _workbench.ActiveView = view;
        switch (view)
        {
            case ERenderPipelineWorkbenchView.Flow:
                DrawWorkbenchFlowView();
                break;
            case ERenderPipelineWorkbenchView.Passes:
                DrawWorkbenchPassesView();
                break;
            case ERenderPipelineWorkbenchView.Resources:
                DrawWorkbenchResourcesView();
                break;
            case ERenderPipelineWorkbenchView.Execution:
                DrawWorkbenchExecutionView();
                break;
            case ERenderPipelineWorkbenchView.Settings:
                DrawWorkbenchSettingsView();
                break;
            case ERenderPipelineWorkbenchView.Validation:
                DrawWorkbenchValidationView();
                break;
            case ERenderPipelineWorkbenchView.Script:
                DrawWorkbenchScriptView();
                break;
        }

        ImGui.EndTabItem();
    }

    /// <summary>
    /// Returns the pinned pipeline, or the pipeline of the first active viewport. The active pipeline is
    /// looked up at most twice each second so an unpinned Workbench does not walk viewports every frame.
    /// </summary>
    private static RenderPipeline? ResolveWorkbenchPipeline()
    {
        if (_workbench.PinnedPipeline is { } pinned)
            return pinned;

        long now = Stopwatch.GetTimestamp();
        bool stale = _workbenchFollowedPipeline is null ||
            _workbenchFollowedPipeline.IsDestroyed ||
            Stopwatch.GetElapsedTime(_workbenchFollowRefreshTimestamp, now).TotalMilliseconds >= WorkbenchFollowRefreshIntervalMs;
        if (!stale)
            return _workbenchFollowedPipeline;

        _workbenchFollowRefreshTimestamp = now;
        _workbenchFollowedPipeline = FindActiveRenderPipeline();
        return _workbenchFollowedPipeline;
    }

    private static RenderPipeline? FindActiveRenderPipeline()
    {
        foreach (XRViewport? viewport in RuntimeEngine.EnumerateActiveViewports())
        {
            if (viewport?.RenderPipeline is { } pipeline)
                return pipeline;
            if (viewport?.ActiveCamera?.RenderPipeline is { } cameraPipeline)
                return cameraPipeline;
        }

        return null;
    }

    /// <summary>
    /// Returns the pinned instance when it still belongs to <paramref name="pipeline"/>. A destroyed instance
    /// falls back to the live instance at the same list position, then to the first live instance.
    /// </summary>
    private static XRRenderPipelineInstance? ResolveWorkbenchInstance(RenderPipeline pipeline)
    {
        IReadOnlyList<XRRenderPipelineInstance> instances = pipeline.Instances;
        if (instances.Count == 0)
            return null;

        XRRenderPipelineInstance? pinned = _workbench.PinnedInstance;
        if (pinned is not null)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                if (!ReferenceEquals(instances[i], pinned))
                    continue;

                _workbench.LastInstanceIndex = i;
                return pinned;
            }
        }

        int index = Math.Clamp(_workbench.LastInstanceIndex, 0, instances.Count - 1);
        return instances[index];
    }
}
