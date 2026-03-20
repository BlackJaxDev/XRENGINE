using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using XREngine.Data.Core;
using XREngine.Data.Profiling;
using XREngine.Profiler.UI;

namespace XREngine.Editor;

/// <summary>
/// Thin wrapper around the shared <see cref="ProfilerPanelRenderer"/>.
/// Uses <see cref="EngineProfilerDataSource"/> for in-process engine data.
/// Provides F11 toggle, UDP-sending toggle, and a button to launch the
/// external profiler application.
/// </summary>
public static partial class EditorImGuiUI
{
    private const string ProfilerDockSpaceWindowId = "Profiler Dockspace";
    private const string ProfilerDockSpaceId = "ProfilerDockSpace";

    internal static void HandleProfilerToggleHotkey()
    {
        var io = ImGui.GetIO();
        if ((io.WantCaptureKeyboard || io.WantTextInput) && ImGui.IsKeyPressed(ImGuiKey.F11, false))
            HandleProfilerToggleShortcutFromImGui();
    }

    internal static void HandleProfilerToggleShortcutFromImGui()
    {
        if (EditorUnitTests.Toggles.EditorType == EditorUnitTests.UnitTestEditorType.IMGUI)
        {
            ToggleProfilerVisible();
            return;
        }

        if (Engine.State.MainPlayer?.ControlledPawnComponent is EditorFlyingCameraPawnComponent pawn)
            pawn.ToggleProfilerShortcutFromImGui();
        else
            ToggleProfilerVisible();
    }

    internal static void SetProfilerVisible(bool visible)
    {
        bool changed = _showProfiler != visible;
        _showProfiler = visible;

        if (visible && changed)
            RequestProfilerDockLayoutReset();
    }

    internal static void ToggleProfilerVisible()
    {
        _showProfiler = !_showProfiler;
        if (_showProfiler)
            RequestProfilerDockLayoutReset();
    }

    internal static void RenderProfilerOverlay()
    {
        HandleProfilerToggleHotkey();
        if (!_showProfiler)
            return;

        EnsureProfessionalImGuiStyling();
        DrawProfilerPanel();
    }

    // Shared renderer + data source (created on first use)
    private static EngineProfilerDataSource? _engineProfilerDataSource;
    private static ProfilerPanelRenderer? _engineProfilerRenderer;

    // Panel visibility toggles (controlled by _showProfiler in ImGui.cs)
    private static bool _showProfilerTree = true;
    private static bool _showFpsDropSpikes = true;
    private static bool _showRenderStats = true;
    private static bool _showGpuPipeline = true;
    private static bool _showThreadAllocations = true;
    private static bool _showComponentTimings = true;
    private static bool _showBvhMetrics = true;
    private static bool _showJobSystem = true;
    private static bool _showMainThreadInvokes = true;
    private static bool _profilerDockLayoutInitialized;
    private static bool _profilerDockLayoutRequested;

    /// <summary>Whether UDP profiler sending is active (disables in-editor panels).</summary>
    private static bool _profilerUdpEnabled;

    private static void SyncProfilerRendererFromPreferences(EditorDebugOptions debug)
    {
        if (_engineProfilerRenderer is null)
            return;

        _engineProfilerRenderer.Paused = debug.ProfilerPanelPaused;
        _engineProfilerRenderer.SortByTime = debug.ProfilerPanelSortByTime;
        _engineProfilerRenderer.SmoothingAlpha = debug.ProfilerPanelSmoothingAlpha;
        _engineProfilerRenderer.UpdateIntervalSeconds = debug.ProfilerPanelUpdateIntervalSeconds;
        _engineProfilerRenderer.PersistenceSeconds = debug.ProfilerPanelPersistenceSeconds;
        _engineProfilerRenderer.GraphSampleCount = debug.ProfilerPanelGraphSampleCount;
        _engineProfilerRenderer.RootHierarchyMinMs = debug.ProfilerPanelRootHierarchyMinMs;
        _engineProfilerRenderer.RootHierarchyMaxMs = debug.ProfilerPanelRootHierarchyMaxMs;
        _engineProfilerRenderer.ShowCpuTimingRawMsLine = debug.ProfilerPanelShowCpuTimingRawMsLine;
        _engineProfilerRenderer.ShowCpuTimingSmoothedMsLine = debug.ProfilerPanelShowCpuTimingSmoothedMsLine;
        _engineProfilerRenderer.InterpolateCpuTimingGraphs = debug.ProfilerPanelInterpolateCpuTimingGraphs;
        _engineProfilerRenderer.CpuTimingDisplayMode = debug.ProfilerPanelCpuTimingDisplayMode;
        _engineProfilerRenderer.ShowGpuTimingRawMsLine = debug.ProfilerPanelShowGpuTimingRawMsLine;
        _engineProfilerRenderer.ShowGpuTimingSmoothedMsLine = debug.ProfilerPanelShowGpuTimingSmoothedMsLine;
        _engineProfilerRenderer.InterpolateGpuTimingGraphs = debug.ProfilerPanelInterpolateGpuTimingGraphs;
        _engineProfilerRenderer.GpuTimingDisplayMode = debug.ProfilerPanelGpuTimingDisplayMode;
    }

    private static void SyncProfilerPanelVisibilityFromPreferences(EditorDebugOptions debug)
    {
        _showProfilerTree = debug.ProfilerPanelShowTree;
        _showFpsDropSpikes = debug.ProfilerPanelShowFpsDropSpikes;
        _showRenderStats = debug.ProfilerPanelShowRenderStats;
        _showGpuPipeline = debug.ProfilerPanelShowGpuPipeline;
        _showThreadAllocations = debug.ProfilerPanelShowThreadAllocations;
        _showComponentTimings = debug.ProfilerPanelShowComponentTimings;
        _showBvhMetrics = debug.ProfilerPanelShowBvhMetrics;
        _showJobSystem = debug.ProfilerPanelShowJobSystem;
        _showMainThreadInvokes = debug.ProfilerPanelShowMainThreadInvokes;
    }

    private static void PersistProfilerRendererSettings()
    {
        if (_engineProfilerRenderer is null)
            return;

        PersistProfilerDebugSetting(_engineProfilerRenderer.Paused,
            static debug => debug.ProfilerPanelPaused,
            static overrides => overrides.ProfilerPanelPausedOverride,
            static (debug, value) => debug.ProfilerPanelPaused = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.SortByTime,
            static debug => debug.ProfilerPanelSortByTime,
            static overrides => overrides.ProfilerPanelSortByTimeOverride,
            static (debug, value) => debug.ProfilerPanelSortByTime = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.SmoothingAlpha,
            static debug => debug.ProfilerPanelSmoothingAlpha,
            static overrides => overrides.ProfilerPanelSmoothingAlphaOverride,
            static (debug, value) => debug.ProfilerPanelSmoothingAlpha = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.UpdateIntervalSeconds,
            static debug => debug.ProfilerPanelUpdateIntervalSeconds,
            static overrides => overrides.ProfilerPanelUpdateIntervalSecondsOverride,
            static (debug, value) => debug.ProfilerPanelUpdateIntervalSeconds = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.PersistenceSeconds,
            static debug => debug.ProfilerPanelPersistenceSeconds,
            static overrides => overrides.ProfilerPanelPersistenceSecondsOverride,
            static (debug, value) => debug.ProfilerPanelPersistenceSeconds = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.GraphSampleCount,
            static debug => debug.ProfilerPanelGraphSampleCount,
            static overrides => overrides.ProfilerPanelGraphSampleCountOverride,
            static (debug, value) => debug.ProfilerPanelGraphSampleCount = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.RootHierarchyMinMs,
            static debug => debug.ProfilerPanelRootHierarchyMinMs,
            static overrides => overrides.ProfilerPanelRootHierarchyMinMsOverride,
            static (debug, value) => debug.ProfilerPanelRootHierarchyMinMs = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.RootHierarchyMaxMs,
            static debug => debug.ProfilerPanelRootHierarchyMaxMs,
            static overrides => overrides.ProfilerPanelRootHierarchyMaxMsOverride,
            static (debug, value) => debug.ProfilerPanelRootHierarchyMaxMs = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.ShowCpuTimingRawMsLine,
            static debug => debug.ProfilerPanelShowCpuTimingRawMsLine,
            static overrides => overrides.ProfilerPanelShowCpuTimingRawMsLineOverride,
            static (debug, value) => debug.ProfilerPanelShowCpuTimingRawMsLine = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.ShowCpuTimingSmoothedMsLine,
            static debug => debug.ProfilerPanelShowCpuTimingSmoothedMsLine,
            static overrides => overrides.ProfilerPanelShowCpuTimingSmoothedMsLineOverride,
            static (debug, value) => debug.ProfilerPanelShowCpuTimingSmoothedMsLine = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.InterpolateCpuTimingGraphs,
            static debug => debug.ProfilerPanelInterpolateCpuTimingGraphs,
            static overrides => overrides.ProfilerPanelInterpolateCpuTimingGraphsOverride,
            static (debug, value) => debug.ProfilerPanelInterpolateCpuTimingGraphs = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.CpuTimingDisplayMode,
            static debug => debug.ProfilerPanelCpuTimingDisplayMode,
            static overrides => overrides.ProfilerPanelCpuTimingDisplayModeOverride,
            static (debug, value) => debug.ProfilerPanelCpuTimingDisplayMode = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.ShowGpuTimingRawMsLine,
            static debug => debug.ProfilerPanelShowGpuTimingRawMsLine,
            static overrides => overrides.ProfilerPanelShowGpuTimingRawMsLineOverride,
            static (debug, value) => debug.ProfilerPanelShowGpuTimingRawMsLine = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.ShowGpuTimingSmoothedMsLine,
            static debug => debug.ProfilerPanelShowGpuTimingSmoothedMsLine,
            static overrides => overrides.ProfilerPanelShowGpuTimingSmoothedMsLineOverride,
            static (debug, value) => debug.ProfilerPanelShowGpuTimingSmoothedMsLine = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.InterpolateGpuTimingGraphs,
            static debug => debug.ProfilerPanelInterpolateGpuTimingGraphs,
            static overrides => overrides.ProfilerPanelInterpolateGpuTimingGraphsOverride,
            static (debug, value) => debug.ProfilerPanelInterpolateGpuTimingGraphs = value);
        PersistProfilerDebugSetting(_engineProfilerRenderer.GpuTimingDisplayMode,
            static debug => debug.ProfilerPanelGpuTimingDisplayMode,
            static overrides => overrides.ProfilerPanelGpuTimingDisplayModeOverride,
            static (debug, value) => debug.ProfilerPanelGpuTimingDisplayMode = value);
    }

    private static void PersistProfilerPanelVisibilitySettings()
    {
        PersistProfilerDebugSetting(_showProfilerTree,
            static debug => debug.ProfilerPanelShowTree,
            static overrides => overrides.ProfilerPanelShowTreeOverride,
            static (debug, value) => debug.ProfilerPanelShowTree = value);
        PersistProfilerDebugSetting(_showFpsDropSpikes,
            static debug => debug.ProfilerPanelShowFpsDropSpikes,
            static overrides => overrides.ProfilerPanelShowFpsDropSpikesOverride,
            static (debug, value) => debug.ProfilerPanelShowFpsDropSpikes = value);
        PersistProfilerDebugSetting(_showRenderStats,
            static debug => debug.ProfilerPanelShowRenderStats,
            static overrides => overrides.ProfilerPanelShowRenderStatsOverride,
            static (debug, value) => debug.ProfilerPanelShowRenderStats = value);
        PersistProfilerDebugSetting(_showGpuPipeline,
            static debug => debug.ProfilerPanelShowGpuPipeline,
            static overrides => overrides.ProfilerPanelShowGpuPipelineOverride,
            static (debug, value) => debug.ProfilerPanelShowGpuPipeline = value);
        PersistProfilerDebugSetting(_showThreadAllocations,
            static debug => debug.ProfilerPanelShowThreadAllocations,
            static overrides => overrides.ProfilerPanelShowThreadAllocationsOverride,
            static (debug, value) => debug.ProfilerPanelShowThreadAllocations = value);
        PersistProfilerDebugSetting(_showComponentTimings,
            static debug => debug.ProfilerPanelShowComponentTimings,
            static overrides => overrides.ProfilerPanelShowComponentTimingsOverride,
            static (debug, value) => debug.ProfilerPanelShowComponentTimings = value);
        PersistProfilerDebugSetting(_showBvhMetrics,
            static debug => debug.ProfilerPanelShowBvhMetrics,
            static overrides => overrides.ProfilerPanelShowBvhMetricsOverride,
            static (debug, value) => debug.ProfilerPanelShowBvhMetrics = value);
        PersistProfilerDebugSetting(_showJobSystem,
            static debug => debug.ProfilerPanelShowJobSystem,
            static overrides => overrides.ProfilerPanelShowJobSystemOverride,
            static (debug, value) => debug.ProfilerPanelShowJobSystem = value);
        PersistProfilerDebugSetting(_showMainThreadInvokes,
            static debug => debug.ProfilerPanelShowMainThreadInvokes,
            static overrides => overrides.ProfilerPanelShowMainThreadInvokesOverride,
            static (debug, value) => debug.ProfilerPanelShowMainThreadInvokes = value);
    }

    private static void PersistProfilerDebugSetting<T>(
        T value,
        Func<EditorDebugOptions, T> effectiveSelector,
        Func<EditorDebugOverrides, OverrideableSetting<T>> overrideSelector,
        Action<EditorDebugOptions, T> globalSetter)
    {
        EditorDebugOptions effective = Engine.EditorPreferences.Debug;
        if (EqualityComparer<T>.Default.Equals(effectiveSelector(effective), value))
            return;

        EditorDebugOverrides? overrides = Engine.EditorPreferencesOverrides?.Debug;
        if (overrides is not null)
        {
            OverrideableSetting<T> overrideSetting = overrideSelector(overrides);
            if (overrideSetting.HasOverride)
            {
                overrideSetting.Value = value;
                return;
            }
        }

        globalSetter(Engine.GlobalEditorPreferences.Debug, value);
    }

    private static void EnsureProfilerInitialized()
    {
        if (_engineProfilerDataSource is not null) return;
        _engineProfilerDataSource = new EngineProfilerDataSource();
        _engineProfilerRenderer = new ProfilerPanelRenderer(_engineProfilerDataSource);
    }

    /// <summary>
    /// Main entry point called from <see cref="RenderEditor"/>.
    /// Draws the in-editor profiler window when <c>_showProfiler</c> is true.
    /// </summary>
    private static void DrawProfilerPanel()
    {
        if (!_showProfiler) return;

        EditorDebugOptions debug = Engine.EditorPreferences.Debug;
        _profilerUdpEnabled = debug.EnableProfilerUdpSending;
        EnsureProfilerInitialized();
        SyncProfilerRendererFromPreferences(debug);
        SyncProfilerPanelVisibilityFromPreferences(debug);
        DrawProfilerDockSpace();

        if (!_showProfiler)
            return;

        // If UDP sending is active, show a thin notice instead of the full panels.
        if (_profilerUdpEnabled)
        {
            if (!ImGui.Begin("Settings"))
            {
                ImGui.End();
                return;
            }

            ImGui.TextColored(new Vector4(0.3f, 0.85f, 1f, 1f),
                "UDP profiler sending is active — use the external profiler for live telemetry.");
            ImGui.Spacing();

            DrawLaunchExternalProfilerButton();

            ImGui.Spacing();
            if (ImGui.Button("Disable UDP Sending"))
            {
                _profilerUdpEnabled = false;
                PersistProfilerDebugSetting(false,
                    static current => current.EnableProfilerUdpSending,
                    static overrides => overrides.EnableProfilerUdpSendingOverride,
                    static (global, value) => global.EnableProfilerUdpSending = value);
            }

            ImGui.End();
            return;
        }

        // ── Normal in-editor profiler ──

        // Collect fresh data from engine statics
        _engineProfilerDataSource!.CollectFromEngine();
        _engineProfilerRenderer!.ProcessLatestData();

        // Controls / toggles window
        if (ImGui.Begin("Settings"))
        {
            // ── Graph / Display Settings (shared) ──
            _engineProfilerRenderer!.DrawSettingsContent();
            PersistProfilerRendererSettings();

            ImGui.Separator();

            // ── Engine Data Collection ──
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Data Collection:");

            bool enableFrameLogging = Engine.EditorPreferences.Debug.EnableProfilerFrameLogging;
            if (ImGui.Checkbox("Frame Logging", ref enableFrameLogging))
                PersistProfilerDebugSetting(enableFrameLogging,
                    static current => current.EnableProfilerFrameLogging,
                    static overrides => overrides.EnableProfilerFrameLoggingOverride,
                    static (global, value) => global.EnableProfilerFrameLogging = value);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, profiler method timing is skipped to reduce overhead.");

            ImGui.SameLine();
            bool enableComponentTiming = Engine.EditorPreferences.Debug.EnableProfilerComponentTiming;
            if (ImGui.Checkbox("Component Timing", ref enableComponentTiming))
                PersistProfilerDebugSetting(enableComponentTiming,
                    static current => current.EnableProfilerComponentTiming,
                    static overrides => overrides.EnableProfilerComponentTimingOverride,
                    static (global, value) => global.EnableProfilerComponentTiming = value);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, per-component tick timings are not recorded for the Components panel.");

            ImGui.SameLine();
            bool enableStatsTracking = Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking;
            if (ImGui.Checkbox("Stats Tracking", ref enableStatsTracking))
                PersistProfilerDebugSetting(enableStatsTracking,
                    static current => current.EnableRenderStatisticsTracking,
                    static overrides => overrides.EnableRenderStatisticsTrackingOverride,
                    static (global, value) => global.EnableRenderStatisticsTracking = value);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, per-frame render statistics (draw calls, triangles) are not tracked.");

            ImGui.SameLine();
            bool enableGpuPipelineProfiling = Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;
            if (ImGui.Checkbox("GPU Pipeline", ref enableGpuPipelineProfiling))
                PersistProfilerDebugSetting(enableGpuPipelineProfiling,
                    static current => current.EnableGpuRenderPipelineProfiling,
                    static overrides => overrides.EnableGpuRenderPipelineProfilingOverride,
                    static (global, value) => global.EnableGpuRenderPipelineProfiling = value);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Collect GPU timestamp timings for generic render-pipeline commands when supported by the active renderer.");

            ImGui.SameLine();
            bool enableAllocTracking = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking;
            if (ImGui.Checkbox("Alloc Tracking", ref enableAllocTracking))
                PersistProfilerDebugSetting(enableAllocTracking,
                    static current => current.EnableThreadAllocationTracking,
                    static overrides => overrides.EnableThreadAllocationTrackingOverride,
                    static (global, value) => global.EnableThreadAllocationTracking = value);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, GC allocation deltas are not measured per tick/frame.");

            ImGui.SameLine();
            bool enableInvokeDiagnostics = Engine.EditorPreferences.Debug.EnableMainThreadInvokeDiagnostics;
            if (ImGui.Checkbox("Invoke Diagnostics", ref enableInvokeDiagnostics))
                PersistProfilerDebugSetting(enableInvokeDiagnostics,
                    static current => current.EnableMainThreadInvokeDiagnostics,
                    static overrides => overrides.EnableMainThreadInvokeDiagnosticsOverride,
                    static (global, value) => global.EnableMainThreadInvokeDiagnostics = value);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Writes verbose main-thread invoke diagnostics with stack traces to disk. Keep this off unless you are actively investigating invoke stalls because it adds overhead.");

            ImGui.Separator();

            // ── External Profiler ──
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "External Profiler:");
            ImGui.SameLine();
            bool udpEnabled = _profilerUdpEnabled;
            if (ImGui.Checkbox("Enable UDP Sending", ref udpEnabled))
            {
                _profilerUdpEnabled = udpEnabled;
                PersistProfilerDebugSetting(udpEnabled,
                    static current => current.EnableProfilerUdpSending,
                    static overrides => overrides.EnableProfilerUdpSendingOverride,
                    static (global, value) => global.EnableProfilerUdpSending = value);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Send profiler telemetry via UDP to the external XREngine.Profiler application.\nWhen enabled, this in-editor profiler panel is disabled to avoid duplicate overhead.");

            ImGui.SameLine();
            DrawLaunchExternalProfilerButton();

            ImGui.Separator();

            // ── Panel Visibility ──
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Panels:");
            ImGui.SameLine(); ImGui.Checkbox("Tree", ref _showProfilerTree);
            ImGui.SameLine(); ImGui.Checkbox("Spikes", ref _showFpsDropSpikes);
            ImGui.SameLine(); ImGui.Checkbox("Render", ref _showRenderStats);
            ImGui.SameLine(); ImGui.Checkbox("GPU", ref _showGpuPipeline);
            ImGui.SameLine(); ImGui.Checkbox("Allocs", ref _showThreadAllocations);
            ImGui.SameLine(); ImGui.Checkbox("Components", ref _showComponentTimings);
            ImGui.SameLine(); ImGui.Checkbox("BVH", ref _showBvhMetrics);
            ImGui.SameLine(); ImGui.Checkbox("Jobs", ref _showJobSystem);
            ImGui.SameLine(); ImGui.Checkbox("Invokes", ref _showMainThreadInvokes);
            PersistProfilerPanelVisibilitySettings();
        }
        ImGui.End();

        // Draw shared profiler panels (no separate settings panel — settings are embedded above)
        bool showSettingsFalse = false;
        _engineProfilerRenderer!.DrawCorePanels(
            ref showSettingsFalse,
            ref _showProfilerTree,
            ref _showFpsDropSpikes,
            ref _showRenderStats,
            ref _showGpuPipeline,
            ref _showThreadAllocations,
            ref _showComponentTimings,
            ref _showBvhMetrics,
            ref _showJobSystem,
            ref _showMainThreadInvokes,
            allowClose: false);
            PersistProfilerPanelVisibilitySettings();
    }

    private static void RequestProfilerDockLayoutReset()
    {
        _profilerDockLayoutInitialized = false;
        _profilerDockLayoutRequested = true;
    }

    private static void DrawProfilerDockSpace()
    {
        var viewport = ImGui.GetMainViewport();
        Vector2 defaultSize = new(viewport.Size.X * 0.78f, viewport.Size.Y * 0.78f);
        Vector2 defaultPos = new(
            viewport.Pos.X + (viewport.Size.X - defaultSize.X) * 0.5f,
            viewport.Pos.Y + (viewport.Size.Y - defaultSize.Y) * 0.5f);

        ImGui.SetNextWindowSize(defaultSize, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver);

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        bool profilerDockOpen = _showProfiler;
        bool dockWindowVisible = ImGui.Begin(ProfilerDockSpaceWindowId, ref profilerDockOpen, flags);

        if (!profilerDockOpen)
        {
            ImGui.End();
            if (_showProfiler)
                HandleProfilerToggleShortcutFromImGui();
            return;
        }

        if (!dockWindowVisible)
        {
            ImGui.End();
            return;
        }

        uint dockSpaceId = ImGui.GetID(ProfilerDockSpaceId);
        ImGui.DockSpace(dockSpaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

        if (_profilerDockLayoutRequested || !_profilerDockLayoutInitialized)
        {
            InitializeProfilerDockingLayout(dockSpaceId, ImGui.GetWindowSize());
            _profilerDockLayoutInitialized = true;
            _profilerDockLayoutRequested = false;
        }

        ImGui.End();
    }

    private static void InitializeProfilerDockingLayout(uint dockSpaceId, Vector2 dockSize)
    {
        float availableWidth = dockSize.X;
        float availableHeight = dockSize.Y;

        ImGuiDockBuilderNative.RemoveNode(dockSpaceId);
        ImGuiDockBuilderNative.AddNode(dockSpaceId, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGuiDockBuilderNative.SetNodeSize(dockSpaceId, new Vector2(availableWidth, availableHeight));

        ImGuiDockBuilderNative.SplitNode(dockSpaceId, ImGuiDir.Left, 0.58f,
            out uint leftMainId, out uint rightDockId);

        ImGuiDockBuilderNative.SplitNode(rightDockId, ImGuiDir.Up, 0.32f,
            out uint rightTopId, out uint rightBottomId);

        ImGuiDockBuilderNative.SplitNode(rightBottomId, ImGuiDir.Up, 0.28f,
            out uint rightUpperBandId, out uint rightLowerBlockId);

        ImGuiDockBuilderNative.SplitNode(rightUpperBandId, ImGuiDir.Right, 0.50f,
            out uint rightUpperRightId, out uint rightUpperLeftId);

        ImGuiDockBuilderNative.SplitNode(rightLowerBlockId, ImGuiDir.Up, 0.42f,
            out uint rightLowerMidId, out uint rightBottomId2);

        ImGuiDockBuilderNative.DockWindow("Settings", rightTopId);
        ImGuiDockBuilderNative.DockWindow("CPU Timings", leftMainId);
        ImGuiDockBuilderNative.DockWindow("GPU Timings", leftMainId);
        ImGuiDockBuilderNative.DockWindow("Render Stats", leftMainId);
        ImGuiDockBuilderNative.DockWindow("Thread Allocations", rightUpperLeftId);
        ImGuiDockBuilderNative.DockWindow("BVH Metrics", rightUpperRightId);
        ImGuiDockBuilderNative.DockWindow("Component Timings", rightLowerMidId);
        ImGuiDockBuilderNative.DockWindow("Job System", rightBottomId2);
        ImGuiDockBuilderNative.DockWindow("Main Thread Invokes", rightBottomId2);
        ImGuiDockBuilderNative.DockWindow("FPS Drop Spikes", rightBottomId2);

        ImGuiDockBuilderNative.Finish(dockSpaceId);
    }

    private static void DrawLaunchExternalProfilerButton()
    {
        if (ImGui.Button("Launch External Profiler"))
        {
            if (!TryLaunchExternalProfiler(out string? error) && !string.IsNullOrWhiteSpace(error))
                Console.Error.WriteLine(error);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Launch the standalone XREngine.Profiler application.\nMake sure UDP sending is enabled to stream data to it.");
    }

    internal static bool TryLaunchExternalProfiler(out string? error)
    {
        try
        {
            string? profilerExe = FindExternalProfilerExe();
            if (!string.IsNullOrWhiteSpace(profilerExe) && File.Exists(profilerExe))
            {
                Process.Start(new ProcessStartInfo(profilerExe!)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(profilerExe) ?? ".",
                });

                error = null;
                return true;
            }

            error = "[Profiler] External profiler not found. Build XREngine.Profiler first.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"[Profiler] Failed to launch external profiler: {ex.Message}";
            return false;
        }
    }

    private static string? FindExternalProfilerExe()
    {
        string? editorDir = AppContext.BaseDirectory;
        var candidates = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(editorDir))
        {
            candidates.Add(Path.Combine(editorDir!, "XREngine.Profiler.exe"));

            string? repoRoot = TryFindRepoRoot(editorDir!);
            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                candidates.Add(Path.Combine(repoRoot!, "XREngine.Profiler", "bin", "Debug", "net10.0-windows7.0", "XREngine.Profiler.exe"));
                candidates.Add(Path.Combine(repoRoot!, "XREngine.Profiler", "bin", "Release", "net10.0-windows7.0", "XREngine.Profiler.exe"));
            }
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string? TryFindRepoRoot(string startDir)
    {
        string? dir = startDir;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            string slnPath = Path.Combine(dir!, "XRENGINE.sln");
            if (File.Exists(slnPath))
                return dir;

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }
}
