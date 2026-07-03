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
        if (visible)
            OpenAllProfilerPanels();
        else
            CloseAllProfilerPanels();
    }

    internal static void ToggleProfilerVisible()
    {
        if (_showProfiler)
            CloseAllProfilerPanels();
        else
            OpenAllProfilerPanels();
    }

    internal static void RenderProfilerOverlay()
    {
        HandleProfilerToggleHotkey();
        if (!_showProfiler)
            return;

        EnsureProfessionalImGuiStyling();
        DrawProfilerPanel();
    }

    internal static void DrawProfilerMenuItems()
    {
        if (!ImGui.BeginMenu("Profiler"))
            return;

        if (ImGui.MenuItem("Open All", "F11"))
            OpenAllProfilerPanels();
        if (ImGui.MenuItem("Close All", null, false, _showProfiler || IsAnyProfilerChildPanelVisible()))
            CloseAllProfilerPanels();

        ImGui.Separator();

        DrawProfilerPanelMenuItem("Settings", ref _showProfilerSettings, persistProfilerPanelVisibility: false);
        DrawProfilerPanelMenuItem("CPU Timings", ref _showProfilerTree, persistProfilerPanelVisibility: true);
        DrawProfilerPanelMenuItem("FPS Drop Spikes", ref _showFpsDropSpikes, persistProfilerPanelVisibility: true);
        DrawProfilerPanelMenuItem("Render Stats", ref _showRenderStats, persistProfilerPanelVisibility: true);
        DrawProfilerPanelMenuItem("GPU Timings", ref _showGpuPipeline, persistProfilerPanelVisibility: true);
        DrawProfilerPanelMenuItem("Thread Allocations", ref _showThreadAllocations, persistProfilerPanelVisibility: true);
        DrawProfilerPanelMenuItem("Component Timings", ref _showComponentTimings, persistProfilerPanelVisibility: true);
        DrawProfilerPanelMenuItem("BVH Metrics", ref _showBvhMetrics, persistProfilerPanelVisibility: true);
        DrawProfilerPanelMenuItem("Job System", ref _showJobSystem, persistProfilerPanelVisibility: true);
        DrawProfilerPanelMenuItem("Main Thread Invokes", ref _showMainThreadInvokes, persistProfilerPanelVisibility: true);

        ImGui.EndMenu();
    }

    private static void DrawProfilerPanelMenuItem(string label, ref bool open, bool persistProfilerPanelVisibility)
    {
        bool wasProfilerVisible = _showProfiler;
        if (!ImGui.MenuItem(label, null, ref open))
            return;

        if (open)
        {
            if (!wasProfilerVisible)
            {
                CloseProfilerChildPanels();
                open = true;
            }

            _showProfiler = true;
            _profilerPanelVisibilityInitialized = true;
        }
        else if (!IsAnyProfilerChildPanelVisible())
        {
            _showProfiler = false;
        }

        if (persistProfilerPanelVisibility)
            PersistProfilerPanelVisibilitySettings();
    }

    private static void OpenAllProfilerPanels()
    {
        _showProfiler = true;
        _showProfilerSettings = true;
        _showProfilerTree = true;
        _showFpsDropSpikes = true;
        _showRenderStats = true;
        _showGpuPipeline = true;
        _showThreadAllocations = true;
        _showComponentTimings = true;
        _showBvhMetrics = true;
        _showJobSystem = true;
        _showMainThreadInvokes = true;
        _profilerPanelVisibilityInitialized = true;
        PersistProfilerPanelVisibilitySettings();
    }

    private static void CloseAllProfilerPanels()
    {
        _showProfiler = false;
        CloseProfilerChildPanels();
        _profilerPanelVisibilityInitialized = true;
        PersistProfilerPanelVisibilitySettings();
    }

    private static void CloseProfilerChildPanels()
    {
        _showProfilerSettings = false;
        _showProfilerTree = false;
        _showFpsDropSpikes = false;
        _showRenderStats = false;
        _showGpuPipeline = false;
        _showThreadAllocations = false;
        _showComponentTimings = false;
        _showBvhMetrics = false;
        _showJobSystem = false;
        _showMainThreadInvokes = false;
    }

    private static bool IsAnyProfilerChildPanelVisible()
        => _showProfilerSettings ||
           _showProfilerTree ||
           _showFpsDropSpikes ||
           _showRenderStats ||
           _showGpuPipeline ||
           _showThreadAllocations ||
           _showComponentTimings ||
           _showBvhMetrics ||
           _showJobSystem ||
           _showMainThreadInvokes;

    // Shared renderer + data source (created on first use)
    private static EngineProfilerDataSource? _engineProfilerDataSource;
    private static ProfilerPanelRenderer? _engineProfilerRenderer;

    private static readonly Action ProfilerFrameLoggingHeader = DrawProfilerFrameLoggingHeader;
    private static readonly Action ProfilerComponentTimingHeader = DrawProfilerComponentTimingHeader;
    private static readonly Action ProfilerRenderStatsHeader = DrawProfilerRenderStatsHeader;
    private static readonly Action ProfilerGpuTimingHeader = DrawProfilerGpuTimingHeader;
    private static readonly Action ProfilerThreadAllocationHeader = DrawProfilerThreadAllocationHeader;
    private static readonly Action ProfilerMainThreadInvokeHeader = DrawProfilerMainThreadInvokeHeader;

    // Panel visibility toggles. _showProfiler is the group gate/F11 state, not a window.
    private static bool _showProfilerSettings;
    private static bool _showProfilerTree;
    private static bool _showFpsDropSpikes;
    private static bool _showRenderStats;
    private static bool _showGpuPipeline;
    private static bool _showThreadAllocations;
    private static bool _showComponentTimings;
    private static bool _showBvhMetrics;
    private static bool _showJobSystem;
    private static bool _showMainThreadInvokes;
    private static bool _profilerPanelVisibilityInitialized;
    private static int _speedProfileDurationSeconds = 15;
    private static string _speedProfileStatus = string.Empty;
    private static string _speedProfileLastSummaryPath = string.Empty;

    // Throttled engine-data collection: collecting deep profiler trees + a ~100-field render-stats
    // packet every frame was the #1 in-editor stall source (~7s total / 644ms max in
    // session 2026-05-11 22:55:55). CollectFromEngine now runs on the app thread
    // (Engine.Time.Timer.UpdateFrame) at ~10Hz - the same collectors are already invoked from the
    // BelowNormal UdpProfilerSender background thread, so off-render-thread collection is safe.
    // ProcessLatestData stays on the render thread because it mutates display state that the
    // panel draw calls read directly; it self-skips when no new frame has been collected.
    private const long ProfilerCollectMinIntervalMs = 100;
    private static readonly Stopwatch _profilerCollectClock = Stopwatch.StartNew();
    private static long _lastProfilerCollectMs = long.MinValue;
    private static bool _profilerAppThreadHooked;

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

    private static void DrawProfilerFrameLoggingHeader()
    {
        DrawProfilerFrameLoggingToggle();
        ImGui.Separator();
    }

    private static void DrawProfilerComponentTimingHeader()
    {
        DrawProfilerFrameLoggingToggle();
        ImGui.SameLine();
        DrawProfilerComponentTimingToggle();
        ImGui.Separator();
    }

    private static void DrawProfilerRenderStatsHeader()
    {
        DrawProfilerStatsTrackingToggle();
        ImGui.Separator();
    }

    private static void DrawProfilerGpuTimingHeader()
    {
        DrawProfilerStatsTrackingToggle();
        ImGui.SameLine();
        DrawProfilerGpuPipelineToggle();
        ImGui.Separator();
    }

    private static void DrawProfilerThreadAllocationHeader()
    {
        DrawProfilerAllocationTrackingToggle();
        ImGui.Separator();
    }

    private static void DrawProfilerMainThreadInvokeHeader()
    {
        DrawProfilerInvokeDiagnosticsToggle();
        ImGui.Separator();
    }

    private static void DrawProfilerFrameLoggingToggle()
    {
        bool enabled = Engine.EditorPreferences.Debug.EnableProfilerFrameLogging;
        if (ImGui.Checkbox("Frame Logging", ref enabled))
            PersistProfilerDebugSetting(enabled,
                static current => current.EnableProfilerFrameLogging,
                static overrides => overrides.EnableProfilerFrameLoggingOverride,
                static (global, value) => global.EnableProfilerFrameLogging = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When disabled, profiler method timing is skipped to reduce overhead.");
    }

    private static void DrawProfilerComponentTimingToggle()
    {
        bool enabled = Engine.EditorPreferences.Debug.EnableProfilerComponentTiming;
        if (ImGui.Checkbox("Component Timing", ref enabled))
            PersistProfilerDebugSetting(enabled,
                static current => current.EnableProfilerComponentTiming,
                static overrides => overrides.EnableProfilerComponentTimingOverride,
                static (global, value) => global.EnableProfilerComponentTiming = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When disabled, per-component tick timings are not recorded for the Components panel.");
    }

    private static void DrawProfilerStatsTrackingToggle()
    {
        bool enabled = Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking;
        if (ImGui.Checkbox("Stats Tracking", ref enabled))
            PersistProfilerDebugSetting(enabled,
                static current => current.EnableRenderStatisticsTracking,
                static overrides => overrides.EnableRenderStatisticsTrackingOverride,
                static (global, value) => global.EnableRenderStatisticsTracking = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When disabled, per-frame render statistics (draw calls, triangles) are not tracked.");
    }

    private static void DrawProfilerGpuPipelineToggle()
    {
        bool enabled = Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;
        if (ImGui.Checkbox("GPU Pipeline", ref enabled))
            PersistProfilerDebugSetting(enabled,
                static current => current.EnableGpuRenderPipelineProfiling,
                static overrides => overrides.EnableGpuRenderPipelineProfilingOverride,
                static (global, value) => global.EnableGpuRenderPipelineProfiling = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Collect GPU timestamp timings for generic render-pipeline commands when supported by the active renderer.");
    }

    private static void DrawProfilerAllocationTrackingToggle()
    {
        bool enabled = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking;
        if (ImGui.Checkbox("Alloc Tracking", ref enabled))
            PersistProfilerDebugSetting(enabled,
                static current => current.EnableThreadAllocationTracking,
                static overrides => overrides.EnableThreadAllocationTrackingOverride,
                static (global, value) => global.EnableThreadAllocationTracking = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When disabled, GC allocation deltas are not measured per tick/frame.");
    }

    private static void DrawProfilerInvokeDiagnosticsToggle()
    {
        bool enabled = Engine.EditorPreferences.Debug.EnableMainThreadInvokeDiagnostics;
        if (ImGui.Checkbox("Invoke Diagnostics", ref enabled))
            PersistProfilerDebugSetting(enabled,
                static current => current.EnableMainThreadInvokeDiagnostics,
                static overrides => overrides.EnableMainThreadInvokeDiagnosticsOverride,
                static (global, value) => global.EnableMainThreadInvokeDiagnostics = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Writes verbose main-thread invoke diagnostics with stack traces to disk. Keep this off unless you are actively investigating invoke stalls because it adds overhead.");
    }

    private static void DrawProfilerUdpSendingToggle()
    {
        bool enabled = _profilerUdpEnabled;
        if (ImGui.Checkbox("Enable UDP Sending", ref enabled))
        {
            _profilerUdpEnabled = enabled;
            PersistProfilerDebugSetting(enabled,
                static current => current.EnableProfilerUdpSending,
                static overrides => overrides.EnableProfilerUdpSendingOverride,
                static (global, value) => global.EnableProfilerUdpSending = value);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Send profiler telemetry via UDP to the external XREngine.Profiler application.\nWhen enabled, this in-editor profiler panel is disabled to avoid duplicate overhead.");
    }

    private static void EnsureProfilerInitialized()
    {
        if (_engineProfilerDataSource is not null) return;
        _engineProfilerDataSource = new EngineProfilerDataSource();
        _engineProfilerRenderer = new ProfilerPanelRenderer(_engineProfilerDataSource);
        _engineProfilerRenderer.CpuFrameTimingDumpRequested = DumpCpuFrameTimingHistory;
        _engineProfilerRenderer.GpuPipelineTimingDumpRequested = DumpGpuPipelineTimingHistory;

        if (!_profilerAppThreadHooked)
        {
            // Subscribe once. UpdateFrame fires on the app thread; the handler is cheap when the
            // panel is hidden or UDP sending is active.
            Engine.Time.Timer.UpdateFrame += CollectProfilerDataOnAppThread;
            _profilerAppThreadHooked = true;
        }
    }

    /// <summary>
    /// App-thread tick: throttled deep-snapshot of engine telemetry into the data source.
    /// Keeps CollectFromEngine off the render thread (it used to dominate UI.DrawProfilerPanel
    /// stalls). Safe because the same collectors are used by the BelowNormal UDP profiler thread.
    /// </summary>
    private static void CollectProfilerDataOnAppThread()
    {
        if (!_showProfiler) return;
        if (_profilerUdpEnabled) return;
        if (_engineProfilerDataSource is null) return;
        ProfilerPanelRenderer.PanelVisibility visibility = CaptureProfilerPanelVisibility();
        if (!visibility.NeedsAnyData) return;

        long nowMs = _profilerCollectClock.ElapsedMilliseconds;
        if (_lastProfilerCollectMs != long.MinValue && (nowMs - _lastProfilerCollectMs) < ProfilerCollectMinIntervalMs)
            return;

        _engineProfilerDataSource.CollectFromEngine(visibility);
        _lastProfilerCollectMs = nowMs;
    }

    private static ProfilerPanelRenderer.PanelVisibility CaptureProfilerPanelVisibility()
        => new(
            ProfilerTree: _showProfilerTree,
            FpsDropSpikes: _showFpsDropSpikes,
            RenderStats: _showRenderStats,
            GpuPipeline: _showGpuPipeline,
            ThreadAllocations: _showThreadAllocations,
            ComponentTimings: _showComponentTimings,
            BvhMetrics: _showBvhMetrics,
            JobSystem: _showJobSystem,
            MainThreadInvokes: _showMainThreadInvokes);

    private static ProfilerPanelRenderer.GpuPipelineTimingDumpResult DumpGpuPipelineTimingHistory(string pipelineName)
    {
        ProfilerDiagnosticDumps.DumpResult result = ProfilerDiagnosticDumps.DumpGpuRenderPipelineTimingHistory(pipelineName);
        return new ProfilerPanelRenderer.GpuPipelineTimingDumpResult(result.Success, result.Message);
    }

    private static ProfilerPanelRenderer.CpuFrameTimingDumpResult DumpCpuFrameTimingHistory()
    {
        ProfilerDiagnosticDumps.DumpResult result = ProfilerDiagnosticDumps.DumpCpuFrameTimingHistory();
        return new ProfilerPanelRenderer.CpuFrameTimingDumpResult(result.Success, result.Message);
    }

    private static void DrawSpeedProfileControls()
    {
        bool active = Engine.IsSpeedProfileCaptureActive;
        string lastSummaryPath = Engine.LastSpeedProfileCaptureSummaryPath;
        if (!active &&
            !string.IsNullOrWhiteSpace(lastSummaryPath) &&
            !StringComparer.Ordinal.Equals(lastSummaryPath, _speedProfileLastSummaryPath))
        {
            _speedProfileLastSummaryPath = lastSummaryPath;
            _speedProfileStatus = "Speed profile written.";
        }

        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Speed Profile:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(96.0f);
        int durationSeconds = _speedProfileDurationSeconds;
        if (ImGui.InputInt("Seconds", ref durationSeconds))
            _speedProfileDurationSeconds = Math.Clamp(durationSeconds, 1, 600);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Capture steady-state frame/render/GPU pipeline stats for this many seconds.");

        ImGui.SameLine();
        if (active)
        {
            if (ImGui.Button("Stop Speed Profile"))
            {
                if (Engine.TryStopSpeedProfileCapture(out string summaryPath, out string? error))
                {
                    _speedProfileLastSummaryPath = summaryPath;
                    _speedProfileStatus = "Speed profile written.";
                }
                else
                {
                    _speedProfileStatus = string.IsNullOrWhiteSpace(error) ? "Speed profile stop failed." : error;
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"{Engine.SpeedProfileCaptureSecondsRemaining:0.0}s");
        }
        else if (ImGui.Button("Dump Speed Profile"))
        {
            if (Engine.TryStartSpeedProfileCapture(_speedProfileDurationSeconds, "profiler-panel", out string? error))
            {
                _speedProfileStatus = "Speed profile running.";
            }
            else
            {
                _speedProfileStatus = string.IsNullOrWhiteSpace(error) ? "Speed profile start failed." : error;
            }
        }

        if (!string.IsNullOrWhiteSpace(_speedProfileStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_speedProfileStatus);
            if (!string.IsNullOrWhiteSpace(_speedProfileLastSummaryPath) && ImGui.IsItemHovered())
                ImGui.SetTooltip(_speedProfileLastSummaryPath);
        }
    }

    /// <summary>
    /// Main entry point called from <see cref="RenderEditor"/>.
    /// Draws the in-editor profiler windows when the profiler group is open.
    /// </summary>
    private static void DrawProfilerPanel()
    {
        if (!_showProfiler) return;

        EditorDebugOptions debug = Engine.EditorPreferences.Debug;
        _profilerUdpEnabled = debug.EnableProfilerUdpSending;
        using (Engine.Profiler.Start("UI.DrawProfilerPanel.InitAndSync"))
        {
            EnsureProfilerInitialized();
            SyncProfilerRendererFromPreferences(debug);
            if (!_profilerPanelVisibilityInitialized)
            {
                SyncProfilerPanelVisibilityFromPreferences(debug);
                _profilerPanelVisibilityInitialized = true;
                if (!IsAnyProfilerChildPanelVisible())
                    _showProfilerSettings = true;
            }
        }

        if (!IsAnyProfilerChildPanelVisible())
        {
            _showProfiler = false;
            return;
        }

        // If UDP sending is active, keep local profiler controls available but avoid drawing
        // stale in-editor telemetry while the external profiler owns collection.
        if (_profilerUdpEnabled)
        {
            using (Engine.Profiler.Start("UI.DrawProfilerPanel.UdpNotice"))
            {
                _showProfilerSettings = true;
                DrawProfilerSettingsPanel(showUdpNotice: true);
            }
            return;
        }

        // Normal in-editor profiler.

        // CollectFromEngine runs on the app thread (see CollectProfilerDataOnAppThread).
        // ProcessLatestData stays here because it mutates renderer display state that the
        // subsequent Draw* calls read; it self-skips when no new frame has been collected, so the
        // per-frame cost on this thread is small.
        ProfilerPanelRenderer.PanelVisibility visibility = CaptureProfilerPanelVisibility();
        using (Engine.Profiler.Start("UI.DrawProfilerPanel.ProcessLatestData"))
            _engineProfilerRenderer!.ProcessLatestData(visibility);

        // Controls / toggles window
        using (Engine.Profiler.Start("UI.DrawProfilerPanel.SettingsWindow"))
        {
            if (_showProfilerSettings)
            {
                if (ImGui.Begin("Profiler Settings", ref _showProfilerSettings))
                {
                    _engineProfilerRenderer!.DrawSettingsContent();
                    PersistProfilerRendererSettings();

                    ImGui.Separator();

                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Data Collection:");
                    DrawProfilerFrameLoggingToggle();
                    ImGui.SameLine();
                    DrawProfilerComponentTimingToggle();
                    ImGui.SameLine();
                    DrawProfilerStatsTrackingToggle();
                    ImGui.SameLine();
                    DrawProfilerGpuPipelineToggle();
                    ImGui.SameLine();
                    DrawProfilerAllocationTrackingToggle();
                    ImGui.SameLine();
                    DrawProfilerInvokeDiagnosticsToggle();

                    ImGui.Separator();

                    DrawSpeedProfileControls();

                    ImGui.Separator();

                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "External Profiler:");
                    ImGui.SameLine();
                    DrawProfilerUdpSendingToggle();
                    ImGui.SameLine();
                    DrawLaunchExternalProfilerButton();
                }
                ImGui.End();
            }
        }

        // Draw shared profiler panels.
        using (Engine.Profiler.Start("UI.DrawProfilerPanel.CorePanels"))
        {
            _engineProfilerRenderer!.DrawProfilerTreePanel(ref _showProfilerTree, drawHeader: ProfilerFrameLoggingHeader);
            _engineProfilerRenderer!.DrawFpsDropSpikesPanel(ref _showFpsDropSpikes, drawHeader: ProfilerFrameLoggingHeader);
            _engineProfilerRenderer!.DrawRenderStatsPanel(ref _showRenderStats, drawHeader: ProfilerRenderStatsHeader);
            _engineProfilerRenderer!.DrawGpuPipelinePanel(ref _showGpuPipeline, drawHeader: ProfilerGpuTimingHeader);
            _engineProfilerRenderer!.DrawThreadAllocationsPanel(ref _showThreadAllocations, drawHeader: ProfilerThreadAllocationHeader);
            _engineProfilerRenderer!.DrawComponentTimingsPanel(ref _showComponentTimings, drawHeader: ProfilerComponentTimingHeader);
            _engineProfilerRenderer!.DrawBvhMetricsPanel(ref _showBvhMetrics);
            _engineProfilerRenderer!.DrawJobSystemPanel(ref _showJobSystem);
            _engineProfilerRenderer!.DrawMainThreadInvokesPanel(ref _showMainThreadInvokes, drawHeader: ProfilerMainThreadInvokeHeader);
            PersistProfilerPanelVisibilitySettings();
        }

        if (!IsAnyProfilerChildPanelVisible())
            _showProfiler = false;
    }

    private static void DrawProfilerSettingsPanel(bool showUdpNotice)
    {
        if (!_showProfilerSettings)
            return;

        if (!ImGui.Begin("Profiler Settings", ref _showProfilerSettings))
        {
            ImGui.End();
            return;
        }

        if (showUdpNotice)
        {
            ImGui.TextColored(new Vector4(0.3f, 0.85f, 1f, 1f),
                "UDP profiler sending is active - use the external profiler for live telemetry.");
            ImGui.Spacing();
            if (ImGui.Button("Disable UDP Sending"))
            {
                _profilerUdpEnabled = false;
                PersistProfilerDebugSetting(false,
                    static current => current.EnableProfilerUdpSending,
                    static overrides => overrides.EnableProfilerUdpSendingOverride,
                    static (global, value) => global.EnableProfilerUdpSending = value);
            }
            ImGui.Separator();
        }

        DrawProfilerUdpSendingToggle();
        ImGui.SameLine();
        DrawLaunchExternalProfilerButton();

        ImGui.End();
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
