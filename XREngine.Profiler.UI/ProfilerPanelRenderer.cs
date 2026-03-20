using System.Numerics;
using ImGuiNET;
using XREngine.Data.Profiling;

namespace XREngine.Profiler.UI;

/// <summary>
/// Shared profiler panel renderer — all aggregation, caching, and ImGui drawing
/// logic. Works with any <see cref="IProfilerDataSource"/> implementation
/// (in-process engine data or remote UDP packets).
/// </summary>
public sealed class ProfilerPanelRenderer(IProfilerDataSource source)
{
    private readonly IProfilerDataSource _source = source;

    // ──────────────── Root method aggregation cache ────────────────

    private readonly Dictionary<string, ProfilerRootMethodAggregate> _rootMethodCache = new();
    private float _persistenceSeconds = 5.0f;
    private float _updateIntervalSeconds = 0.5f;
    private float _lastEnqueuedFrameTime = float.NegativeInfinity;
    private DateTime _lastDisplayRefreshUtc = DateTime.MinValue;

    // Worst-frame rolling window
    private ProfilerFramePacket? _worstFrameWindowSnapshot;
    private float _worstFrameWindowMaxMs;
    private DateTime _worstFrameWindowStart = DateTime.MinValue;
    private ProfilerFramePacket? _worstFrameDisplaySnapshot;
    private float _worstFrameDisplayMs;
    private static readonly TimeSpan WorstFrameWindowDuration = TimeSpan.FromSeconds(0.5);

    // Display state
    private ProfilerFramePacket? _lastHierarchySnapshot;
    private float _lastHierarchyFrameMs;
    private bool _lastHierarchyUsingWorst;
    private float _lastCaptureTime;
    private Dictionary<int, float[]> _lastHistorySnapshot = new();

    // FPS drop spike tracking
    private const int FpsSpikeBaselineWindowSamples = 30;
    private const float FpsSpikeMinPreviousFps = 10.0f;
    private const float FpsSpikeMinDeltaMs = 1.0f;
    private float _fpsSpikeMinDropFps = 15.0f;
    private float _lastFpsSpikeProcessedFrameTime = float.NegativeInfinity;
    private readonly List<FpsDropSpikePathEntry> _fpsDropSpikePaths = new();
    private readonly Dictionary<string, int> _fpsDropSpikePathIndexByKey = new(StringComparer.Ordinal);
    private readonly List<int> _fpsDropSpikeSortedIndices = new();
    private int _fpsDropSpikeLastSortedCount;
    private int _fpsDropSpikeLastSortColumn = -1;
    private ImGuiSortDirection _fpsDropSpikeLastSortDirection = ImGuiSortDirection.None;

    // Thread cache
    private readonly Dictionary<int, ProfilerThreadCacheEntry> _threadCache = new();
    private static readonly TimeSpan ThreadStaleThreshold = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan ThreadCacheTimeout = TimeSpan.FromSeconds(15.0);
    private readonly List<int> _threadKeysToRemove = new();

    // Cached sorted lists
    private readonly List<ProfilerRootMethodAggregate> _cachedGraphList = new();
    private readonly List<ProfilerRootMethodAggregate> _cachedHierarchyList = new();
    private readonly List<string> _rootKeysToRemove = new();
    private readonly List<string> _childKeysToRemove = new();

    // UI state
    private bool _paused;
    private bool _sortByTime;
    private float _smoothingAlpha;
    private int _graphSampleCount = 120;
    private float _rootHierarchyMinMs;
    private float _rootHierarchyMaxMs;
    private bool _showCpuTimingRawMsLine = true;
    private bool _showCpuTimingSmoothedMsLine = true;
    private bool _interpolateCpuTimingGraphs = true;
    private bool _showGpuTimingRawMsLine = true;
    private bool _showGpuTimingSmoothedMsLine = true;
    private bool _interpolateGpuTimingGraphs = true;

    private static readonly string[] TimingDisplayModeLabels = ["Latest", "Average", "Worst"];
    private ProfilerTimingDisplayMode _cpuTimingDisplayMode = ProfilerTimingDisplayMode.Latest;
    private ProfilerTimingDisplayMode _gpuTimingDisplayMode = ProfilerTimingDisplayMode.Latest;

    private readonly Dictionary<string, bool> _nodeOpenCache = new();

    // Render-stats history (shared by editor + external profiler)
    private const int RenderStatsHistorySamples = 240;
    private readonly float[] _vulkanPipelineBindsHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _vulkanDescriptorBindsHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _vulkanPipelineSkipRateHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _vulkanDescriptorSkipRateHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _vulkanPipelineCacheHitRateHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _vulkanFrameTotalMsHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _vulkanFrameWaitFenceMsHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _vulkanFrameRecordCommandBufferMsHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _vulkanFrameGpuCommandBufferMsHistory = new float[RenderStatsHistorySamples];
    private readonly float[] _renderStatsHistoryScratch = new float[RenderStatsHistorySamples];
    private readonly float[] _renderStatsHistoryRawScratch = new float[RenderStatsHistorySamples];
    private readonly float[] _renderStatsHistoryInterpolatedScratch = new float[RenderStatsHistorySamples];
    private int _renderStatsHistoryHead;
    private int _renderStatsHistoryCount;
    private readonly Dictionary<string, float[]> _gpuPipelineRootHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _gpuPipelineRootHistoryLastSeen = new(StringComparer.Ordinal);
    private readonly List<string> _gpuPipelineRootHistoryStaleKeys = new();
    private readonly float[] _gpuPipelineRootHistoryScratch = new float[RenderStatsHistorySamples];
    private readonly float[] _gpuPipelineRootHistoryRawScratch = new float[RenderStatsHistorySamples];
    private readonly float[] _gpuPipelineRootHistoryInterpolatedScratch = new float[RenderStatsHistorySamples];
    private int _gpuPipelineRootSampleSerial;
    private bool _gpuPipelineDisplayEnabled;
    private bool _gpuPipelineDisplaySupported;
    private bool _gpuPipelineDisplayReady;
    private string _gpuPipelineDisplayBackend = string.Empty;
    private string _gpuPipelineDisplayStatusMessage = string.Empty;
    private double _gpuPipelineDisplayFrameMs;
    private GpuPipelineTimingNodeData[] _gpuPipelineDisplayRoots = [];
    private readonly Dictionary<string, TimingGraphInterpolationState> _cpuTimingInterpolationStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimingGraphInterpolationState> _cpuRootMethodHzInterpolationStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimingGraphInterpolationState> _gpuTimingInterpolationStates = new(StringComparer.Ordinal);
    private readonly float[] _rootMethodHzInterpolatedScratch = new float[720];

    // ═══════════════════════════════════════════════════════════════
    //  Public entry points — called per-frame
    // ═══════════════════════════════════════════════════════════════

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public bool SortByTime
    {
        get => _sortByTime;
        set => _sortByTime = value;
    }

    public float SmoothingAlpha
    {
        get => _smoothingAlpha;
        set => _smoothingAlpha = Math.Clamp(value, 0.0f, 0.95f);
    }

    public float UpdateIntervalSeconds
    {
        get => _updateIntervalSeconds;
        set => _updateIntervalSeconds = Math.Clamp(value, 0.0f, 2.0f);
    }

    public float PersistenceSeconds
    {
        get => _persistenceSeconds;
        set => _persistenceSeconds = Math.Clamp(value, 0.5f, 10.0f);
    }

    public int GraphSampleCount
    {
        get => _graphSampleCount;
        set => _graphSampleCount = Math.Clamp(value, 30, 720);
    }

    public float RootHierarchyMinMs
    {
        get => _rootHierarchyMinMs;
        set => _rootHierarchyMinMs = Math.Clamp(value, 0.0f, 1000.0f);
    }

    public float RootHierarchyMaxMs
    {
        get => _rootHierarchyMaxMs;
        set => _rootHierarchyMaxMs = Math.Clamp(value, 0.0f, 1000.0f);
    }

    public bool ShowCpuTimingRawMsLine
    {
        get => _showCpuTimingRawMsLine;
        set => _showCpuTimingRawMsLine = value;
    }

    public bool ShowCpuTimingSmoothedMsLine
    {
        get => _showCpuTimingSmoothedMsLine;
        set => _showCpuTimingSmoothedMsLine = value;
    }

    public bool InterpolateCpuTimingGraphs
    {
        get => _interpolateCpuTimingGraphs;
        set => _interpolateCpuTimingGraphs = value;
    }

    public ProfilerTimingDisplayMode CpuTimingDisplayMode
    {
        get => _cpuTimingDisplayMode;
        set => _cpuTimingDisplayMode = value;
    }

    public bool ShowGpuTimingRawMsLine
    {
        get => _showGpuTimingRawMsLine;
        set => _showGpuTimingRawMsLine = value;
    }

    public bool ShowGpuTimingSmoothedMsLine
    {
        get => _showGpuTimingSmoothedMsLine;
        set => _showGpuTimingSmoothedMsLine = value;
    }

    public bool InterpolateGpuTimingGraphs
    {
        get => _interpolateGpuTimingGraphs;
        set => _interpolateGpuTimingGraphs = value;
    }

    public ProfilerTimingDisplayMode GpuTimingDisplayMode
    {
        get => _gpuTimingDisplayMode;
        set => _gpuTimingDisplayMode = value;
    }

    /// <summary>Process the latest data from the source (call once per frame).</summary>
    public void ProcessLatestData()
    {
        if (_paused) return;

        var nowUtc = DateTime.UtcNow;
        double updateIntervalSeconds = GetEffectiveUpdateIntervalSeconds();
        var minInterval = updateIntervalSeconds <= 0.0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(updateIntervalSeconds);
        bool shouldRefreshDisplay =
            _lastDisplayRefreshUtc == DateTime.MinValue ||
            minInterval == TimeSpan.Zero ||
            nowUtc - _lastDisplayRefreshUtc >= minInterval;

        var frame = _source.LatestFrame;
        var renderStats = _source.LatestRenderStats;
        bool hasFrame = frame is not null && frame.Threads is { Length: > 0 };
        bool hasNewFrame = false;

        if (hasFrame)
        {
            hasNewFrame = frame!.FrameTime != _lastEnqueuedFrameTime;
            if (hasNewFrame)
            {
                _lastEnqueuedFrameTime = frame.FrameTime;
                var history = frame.ThreadHistory ?? [];
                _lastHistorySnapshot = history;

                UpdateWorstFrameStatistics(frame, nowUtc);
                UpdateThreadCache(frame.Threads!, nowUtc);
                UpdateRootMethodCache(frame, history, nowUtc);
                UpdateFpsDropSpikeLog(frame, history);
            }
        }

        // Push render stats independently of frame logging so GPU/render graphs keep updating
        if (renderStats is not null && shouldRefreshDisplay)
            PushRenderStatsSample(renderStats);

        RefreshThreadCacheState(nowUtc);

        if (shouldRefreshDisplay)
        {
            PruneRootMethodCache(nowUtc);
            UpdateDisplayValues();
            UpdateGpuPipelineDisplay(renderStats);

            if (hasNewFrame)
            {
                var display = GetSnapshotForHierarchy(frame!, out float hierMs, out bool usingWorst);
                _lastCaptureTime = frame!.FrameTime;
                _lastHierarchySnapshot = display;
                _lastHierarchyFrameMs = hierMs;
                _lastHierarchyUsingWorst = usingWorst;
            }

            RebuildCachedRootMethodLists(nowUtc);
            _lastDisplayRefreshUtc = nowUtc;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Panel draw methods
    // ═══════════════════════════════════════════════════════════════

    public void DrawProfilerTreePanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("CPU Timings", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("CPU Timings"))
            {
                ImGui.End();
                return;
            }
        }

        var nowUtc = DateTime.UtcNow;

        // Status
        if (_threadCache.Count == 0)
        {
            ImGui.TextDisabled("No profiler samples captured yet.");
            ImGui.End();
            return;
        }

        var snapshot = _lastHierarchySnapshot;
        if (snapshot is not null)
        {
            ImGui.Text($"Captured at {_lastCaptureTime:F3}s");
            ImGui.Text($"Worst frame (0.5s window): {_lastHierarchyFrameMs:F3} ms");
            if (_lastHierarchyUsingWorst)
                ImGui.Text("Hierarchy shows worst frame snapshot from the rolling window.");
        }
        else
        {
            ImGui.Text($"Awaiting fresh samples… (last capture at {_lastCaptureTime:F3}s)");
            ImGui.Text($"Worst frame (0.5s window): {_worstFrameDisplayMs:F3} ms");
        }

        ImGui.Separator();

        // ── Root Method Graphs ──
        if (ImGui.CollapsingHeader("Root Method Graphs", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled($"Per-call Hz graph (smoothing={_smoothingAlpha:F2})");
            ImGui.TextColored(new Vector4(0.55f, 0.75f, 1.00f, 1.00f), "Raw Hz");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.00f, 0.70f, 0.25f, 1.00f), "Display Hz");
            foreach (var rm in _cachedGraphList)
            {
                bool isStale = nowUtc - rm.LastSeen > TimeSpan.FromSeconds(GetStaleWindowSeconds());
                int calls = Math.Max(1, rm.DisplayRootNodeCount);
                float avgCallMs = rm.DisplayTotalTimeMs / calls;
                string label = calls > 1
                    ? $"{rm.Name} ({avgCallMs:F3} ms/call, {calls} calls, {rm.DisplayTotalTimeMs:F3} ms total, {rm.DisplayThreadCount} thread(s))"
                    : $"{rm.Name} ({rm.DisplayTotalTimeMs:F3} ms, {rm.DisplayThreadCount} thread(s))";
                if (isStale) label += " (inactive)";

                if (isStale) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));
                ImGui.Text(label);
                if (isStale) ImGui.PopStyleColor();

                if (rm.RawHzSamples.Length > 0 && rm.DisplayHzSamples.Length > 0)
                {
                    int sampleCount = Math.Min(rm.RawHzSamples.Length, rm.DisplayHzSamples.Length);
                    float min = float.MaxValue, max = float.MinValue;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        if (_showCpuTimingRawMsLine)
                        {
                            float rawHz = rm.RawHzSamples[i];
                            if (rawHz < min) min = rawHz;
                            if (rawHz > max) max = rawHz;
                        }

                        if (_showCpuTimingSmoothedMsLine)
                        {
                            float displayHz = rm.DisplayHzSamples[i];
                            if (displayHz < min) min = displayHz;
                            if (displayHz > max) max = displayHz;
                        }
                    }
                    if (!float.IsFinite(min) || !float.IsFinite(max)) { min = 0f; max = 0f; }
                    if (MathF.Abs(max - min) < 0.001f) max = min + 0.001f;

                    DrawDualHzPlot(
                        $"##Plot_{rm.Name}",
                        rm.Name,
                        rm.RawHzSamples,
                        rm.DisplayHzSamples,
                        sampleCount,
                        min,
                        max,
                        new Vector2(-1f, 44f),
                        _showCpuTimingRawMsLine,
                        _showCpuTimingSmoothedMsLine,
                        _interpolateCpuTimingGraphs);
                }
            }
        }

        ImGui.Separator();
        ImGui.Text("Hierarchy by Root Method");

        // ── Root Method Hierarchy Table ──
        if (_cachedHierarchyList.Count > 0)
        {
            if (ImGui.BeginTable("ProfilerHierarchy", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.WidthFixed, 40f);
                ImGui.TableHeadersRow();

                foreach (var rm in _cachedHierarchyList)
                {
                    if (rm.DisplayRootNodeCount > 0)
                        DrawAggregatedRootMethodHierarchy(rm, nowUtc);
                }
                ImGui.EndTable();
            }
        }
        else
        {
            ImGui.Text("No root methods captured.");
        }

        ImGui.End();
    }

    public void DrawFpsDropSpikesPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("FPS Drop Spikes", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("FPS Drop Spikes"))
            {
                ImGui.End();
                return;
            }
        }

        if (ImGui.Button("Clear Spikes"))
        {
            _fpsDropSpikePaths.Clear();
            _fpsDropSpikePathIndexByKey.Clear();
            _fpsDropSpikeSortedIndices.Clear();
            _fpsDropSpikeLastSortedCount = 0;
            _fpsDropSpikeLastSortColumn = -1;
            _fpsDropSpikeLastSortDirection = ImGuiSortDirection.None;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(95);
        ImGui.DragFloat("Min Drop FPS", ref _fpsSpikeMinDropFps, 0.5f, 0.0f, 1000.0f);
        ImGui.Text($"Tracked paths: {_fpsDropSpikePaths.Count:N0}");

        if (_fpsDropSpikePaths.Count == 0)
        {
            ImGui.TextDisabled("No spikes detected yet.");
            ImGui.End();
            return;
        }

        float rowH = ImGui.GetTextLineHeightWithSpacing();
        float estH = MathF.Min(12, _fpsDropSpikePaths.Count) * rowH + rowH * 2;

        if (ImGui.BeginTable("FpsDropSpikes", 8,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable,
            new Vector2(-1f, estH)))
        {
            ImGui.TableSetupColumn("Worst Frame (s)", ImGuiTableColumnFlags.WidthFixed, 95f);
            ImGui.TableSetupColumn("Thread", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Ref FPS", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Now FPS", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Drop FPS", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 70f);
            ImGui.TableSetupColumn("Drop", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Hot Path", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            ResolveSpikeSort();

            bool useSorted = _fpsDropSpikeSortedIndices.Count == _fpsDropSpikePaths.Count;
            int count = _fpsDropSpikePaths.Count;
            for (int i = 0; i < count; i++)
            {
                var spike = _fpsDropSpikePaths[useSorted ? _fpsDropSpikeSortedIndices[i] : i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text($"{spike.WorstFrameTimeSeconds:F3}");
                ImGui.TableSetColumnIndex(1); ImGui.Text(spike.ThreadId.ToString());
                ImGui.TableSetColumnIndex(2); ImGui.Text(spike.SeenCount.ToString());
                ImGui.TableSetColumnIndex(3); ImGui.Text($"{spike.WorstComparisonFps:F1}");
                ImGui.TableSetColumnIndex(4); ImGui.Text($"{spike.WorstCurrentFps:F1}");
                ImGui.TableSetColumnIndex(5); ImGui.Text($"{spike.WorstDeltaFps:F1}");
                ImGui.TableSetColumnIndex(6); ImGui.Text($"{spike.WorstDropFraction * 100f:F0}%");
                ImGui.TableSetColumnIndex(7); ImGui.TextWrapped(spike.HotPath);
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    public void DrawRenderStatsPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("Render Stats", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("Render Stats"))
            {
                ImGui.End();
                return;
            }
        }

        var stats = _source.LatestRenderStats;
        if (stats is null)
        {
            ImGui.TextDisabled("Waiting for render stats...");
            ImGui.End();
            return;
        }

        ImGui.Text($"Draw Calls: {stats.DrawCalls:N0}");
        ImGui.Text($"Multi-Draw Calls: {stats.MultiDrawCalls:N0}");
        ImGui.Text($"Triangles Rendered: {stats.TrianglesRendered:N0}");

        if (stats.GpuCpuFallbackEvents > 0 || stats.GpuCpuFallbackRecoveredCommands > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.2f, 1.0f), "GPU/CPU Fallback:");
            ImGui.Text($"  Fallback Events: {stats.GpuCpuFallbackEvents:N0}");
            ImGui.Text($"  Recovered Commands: {stats.GpuCpuFallbackRecoveredCommands:N0}");
        }

        if (stats.GpuTransparencyMaskedVisible > 0 ||
            stats.GpuTransparencyApproximateVisible > 0 ||
            stats.GpuTransparencyExactVisible > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Transparency Domains:");
            ImGui.Text($"  Opaque/Other: {stats.GpuTransparencyOpaqueOrOtherVisible:N0}");
            ImGui.Text($"  Masked: {stats.GpuTransparencyMaskedVisible:N0}");
            ImGui.Text($"  Approximate: {stats.GpuTransparencyApproximateVisible:N0}");
            ImGui.Text($"  Exact: {stats.GpuTransparencyExactVisible:N0}");
        }

        ImGui.Separator();
        ImGui.Text("Vulkan Bind/Cache Churn:");
        ImGui.Text($"  Pipeline Binds: {stats.VulkanPipelineBinds:N0} (skipped {stats.VulkanPipelineBindSkips:N0})");
        ImGui.Text($"  Descriptor Binds: {stats.VulkanDescriptorBinds:N0} (skipped {stats.VulkanDescriptorBindSkips:N0})");
        ImGui.Text($"  Push Constant Writes: {stats.VulkanPushConstantWrites:N0}");
        ImGui.Text($"  Vertex Buffer Binds: {stats.VulkanVertexBufferBinds:N0} (skipped {stats.VulkanVertexBufferBindSkips:N0})");
        ImGui.Text($"  Index Buffer Binds: {stats.VulkanIndexBufferBinds:N0} (skipped {stats.VulkanIndexBufferBindSkips:N0})");
        ImGui.Text($"  Pipeline Cache Lookups: {stats.VulkanPipelineCacheLookupHits:N0} hits / {stats.VulkanPipelineCacheLookupMisses:N0} misses ({stats.VulkanPipelineCacheLookupHitRate * 100.0:F1}% hit)");

        double pipelineSkipRate = (stats.VulkanPipelineBinds + stats.VulkanPipelineBindSkips) <= 0
            ? 0.0
            : (double)stats.VulkanPipelineBindSkips / (stats.VulkanPipelineBinds + stats.VulkanPipelineBindSkips);
        double descriptorSkipRate = (stats.VulkanDescriptorBinds + stats.VulkanDescriptorBindSkips) <= 0
            ? 0.0
            : (double)stats.VulkanDescriptorBindSkips / (stats.VulkanDescriptorBinds + stats.VulkanDescriptorBindSkips);
        ImGui.Text($"  Skip Efficiency: pipeline {pipelineSkipRate * 100.0:F1}% | descriptor {descriptorSkipRate * 100.0:F1}%");

        if (_renderStatsHistoryCount > 1 && ImGui.CollapsingHeader("Vulkan Churn History", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRenderStatsHistoryPlot("Pipeline Binds", _vulkanPipelineBindsHistory, "binds/frame", 0f, 0f);
            DrawRenderStatsHistoryPlot("Descriptor Binds", _vulkanDescriptorBindsHistory, "binds/frame", 0f, 0f);
            DrawRenderStatsHistoryPlot("Pipeline Skip %", _vulkanPipelineSkipRateHistory, "%", 0f, 100f);
            DrawRenderStatsHistoryPlot("Descriptor Skip %", _vulkanDescriptorSkipRateHistory, "%", 0f, 100f);
            DrawRenderStatsHistoryPlot("Pipeline Cache Hit %", _vulkanPipelineCacheHitRateHistory, "%", 0f, 100f);
        }

        ImGui.Separator();
        ImGui.Text("Vulkan Frame Lifecycle Timing:");
        ImGui.Text($"  CPU Total: {GetRingBufferStat(_vulkanFrameTotalMsHistory, _cpuTimingDisplayMode):F3} ms");
        ImGui.Text($"  CPU Wait Fence: {GetRingBufferStat(_vulkanFrameWaitFenceMsHistory, _cpuTimingDisplayMode):F3} ms");
        ImGui.Text($"  CPU Acquire: {stats.VulkanFrameAcquireImageMs:F3} ms");
        ImGui.Text($"  CPU Record CmdBuf: {GetRingBufferStat(_vulkanFrameRecordCommandBufferMsHistory, _cpuTimingDisplayMode):F3} ms");
        ImGui.Text($"  CPU Submit: {stats.VulkanFrameSubmitMs:F3} ms");
        ImGui.Text($"  CPU Trim: {stats.VulkanFrameTrimMs:F3} ms");
        ImGui.Text($"  CPU Present: {stats.VulkanFramePresentMs:F3} ms");
        ImGui.Text($"  GPU CmdBuf: {GetRingBufferStat(_vulkanFrameGpuCommandBufferMsHistory, _cpuTimingDisplayMode):F3} ms");

        if (_renderStatsHistoryCount > 1 && ImGui.CollapsingHeader("Vulkan Frame Timing History", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRenderStatsHistoryPlot("CPU Frame Total", _vulkanFrameTotalMsHistory, "ms", 0f, 0f);
            DrawRenderStatsHistoryPlot("CPU Wait Fence", _vulkanFrameWaitFenceMsHistory, "ms", 0f, 0f);
            DrawRenderStatsHistoryPlot("CPU Record CmdBuf", _vulkanFrameRecordCommandBufferMsHistory, "ms", 0f, 0f);
            DrawRenderStatsHistoryPlot("GPU CmdBuf", _vulkanFrameGpuCommandBufferMsHistory, "ms", 0f, 0f);
        }

        ImGui.Separator();
        ImGui.Text("GPU VRAM Usage:");
        double totalVRAM = stats.AllocatedVRAMBytes / (1024.0 * 1024.0);
        ImGui.Text($"  Total: {totalVRAM:F2} MB");
        ImGui.Text($"  Buffers: {stats.AllocatedBufferBytes / (1024.0 * 1024.0):F2} MB");
        ImGui.Text($"  Textures: {stats.AllocatedTextureBytes / (1024.0 * 1024.0):F2} MB");
        ImGui.Text($"  Render Buffers: {stats.AllocatedRenderBufferBytes / (1024.0 * 1024.0):F2} MB");

        ImGui.Separator();
        ImGui.Text("FBO Render Bandwidth:");
        ImGui.Text($"  Bandwidth: {stats.FBOBandwidthBytes / (1024.0 * 1024.0):F2} MB/frame");
        ImGui.Text($"  FBO Binds: {stats.FBOBindCount:N0}");

        ImGui.Separator();
        ImGui.Text("Render Matrix Updates:");
        if (!stats.RenderMatrixStatsReady)
        {
            ImGui.TextDisabled("  Waiting for stats...");
        }
        else
        {
            ImGui.Text($"  Applied (swap): {stats.RenderMatrixApplied:N0}");
            ImGui.Text($"  SetRenderMatrix calls: {stats.RenderMatrixSetCalls:N0}");
            ImGui.Text($"  Listener invocations: {stats.RenderMatrixListenerInvocations:N0}");

            if (stats.RenderMatrixListenerCounts is { Length: > 0 })
            {
                var sorted = stats.RenderMatrixListenerCounts
                    .Where(e => e is not null)
                    .OrderByDescending(e => e.Count)
                    .Take(10)
                    .ToArray();

                if (sorted.Length > 0)
                {
                    ImGui.Text("  Top listener types:");
                    if (ImGui.BeginTable("RenderMatListeners", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                    {
                        ImGui.TableSetupColumn("Listener");
                        ImGui.TableSetupColumn("Count");
                        ImGui.TableHeadersRow();
                        foreach (var entry in sorted)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0); ImGui.Text(entry.Name ?? "?");
                            ImGui.TableSetColumnIndex(1); ImGui.Text(entry.Count.ToString());
                        }
                        ImGui.EndTable();
                    }
                }
            }
        }

        ImGui.Separator();
        ImGui.Text("Octree Commands:");
        if (!stats.OctreeStatsReady)
        {
            ImGui.TextDisabled("  Waiting for stats...");
        }
        else
        {
            ImGui.Text($"  Add: {stats.OctreeAddCount:N0}");
            ImGui.Text($"  Move: {stats.OctreeMoveCount:N0}");
            ImGui.Text($"  Remove: {stats.OctreeRemoveCount:N0}");
            ImGui.Text($"  Skipped: {stats.OctreeSkippedMoveCount:N0}");
        }

        ImGui.End();
    }

    public void DrawGpuPipelinePanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;

        if (allowClose)
        {
            if (!ImGui.Begin("GPU Timings", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("GPU Timings"))
            {
                ImGui.End();
                return;
            }
        }

        if (_source.LatestRenderStats is null)
        {
            ImGui.TextDisabled("Waiting for GPU pipeline timings...");
            ImGui.End();
            return;
        }

        if (!_gpuPipelineDisplayEnabled)
        {
            ImGui.TextDisabled("GPU render-pipeline profiling is disabled.");
            ImGui.End();
            return;
        }

        if (!_gpuPipelineDisplaySupported)
        {
            string status = string.IsNullOrWhiteSpace(_gpuPipelineDisplayStatusMessage)
                ? "GPU render-pipeline profiling is not supported by the active renderer."
                : _gpuPipelineDisplayStatusMessage;
            ImGui.TextWrapped(status);
            if (!string.IsNullOrWhiteSpace(_gpuPipelineDisplayBackend))
                ImGui.TextDisabled($"Backend: {_gpuPipelineDisplayBackend}");
            ImGui.End();
            return;
        }

        if (!_gpuPipelineDisplayReady || _gpuPipelineDisplayRoots.Length == 0)
        {
            ImGui.TextDisabled("Waiting for the first resolved GPU command frame...");
            if (!string.IsNullOrWhiteSpace(_gpuPipelineDisplayBackend))
                ImGui.TextDisabled($"Backend: {_gpuPipelineDisplayBackend}");
            ImGui.End();
            return;
        }

        ImGui.Text($"Backend: {_gpuPipelineDisplayBackend}");
        ImGui.Text($"Resolved Frame: {_gpuPipelineDisplayFrameMs:F3} ms");
        if (!string.IsNullOrWhiteSpace(_gpuPipelineDisplayStatusMessage))
            ImGui.TextDisabled(_gpuPipelineDisplayStatusMessage);

        if (_gpuPipelineRootHistory.Count > 0 && ImGui.CollapsingHeader("Root History", ImGuiTreeNodeFlags.DefaultOpen))
        {
            GpuPipelineTimingNodeData[] roots = GetOrderedGpuPipelineRoots(_gpuPipelineDisplayRoots);
            for (int i = 0; i < roots.Length; i++)
            {
                GpuPipelineTimingNodeData root = roots[i];
                if (!IsGpuPipelineNodeVisible(root))
                    continue;

                if (!_gpuPipelineRootHistory.TryGetValue(root.Name, out float[]? series))
                    continue;

                int sampleCount = BuildOrderedRenderStatsHistory(series, _gpuPipelineRootHistoryRawScratch, smooth: false);
                if (sampleCount <= 0)
                    continue;

                DrawTimingHistoryPlot(
                    root.Name,
                    $"##GpuPipeline_{i}",
                    _gpuPipelineRootHistoryRawScratch,
                    sampleCount,
                    units: "ms",
                    minY: 0f,
                    maxY: 0f,
                    showRawLine: _showGpuTimingRawMsLine,
                    showSmoothedLine: _showGpuTimingSmoothedMsLine,
                    interpolate: _interpolateGpuTimingGraphs,
                    interpolationStates: _gpuTimingInterpolationStates,
                    smoothedScratch: _gpuPipelineRootHistoryScratch,
                    interpolatedScratch: _gpuPipelineRootHistoryInterpolatedScratch,
                    rawColor: new Vector4(1.00f, 0.78f, 0.40f, 0.78f),
                    smoothedColor: new Vector4(0.95f, 0.58f, 0.18f, 1.0f),
                    smoothedFillColor: new Vector4(0.95f, 0.58f, 0.18f, 0.16f),
                    displayMode: _gpuTimingDisplayMode);
            }
        }

        ImGui.Separator();

        if (ImGui.BeginTable("GpuPipelineHierarchy", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("GPU ms", ImGuiTableColumnFlags.WidthFixed, 85f);
            ImGui.TableSetupColumn("Samples", ImGuiTableColumnFlags.WidthFixed, 65f);
            ImGui.TableSetupColumn("Frame %", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableHeadersRow();

            GpuPipelineTimingNodeData[] orderedRoots = GetOrderedGpuPipelineRoots(_gpuPipelineDisplayRoots);
            for (int i = 0; i < orderedRoots.Length; i++)
                DrawGpuPipelineNode(orderedRoots[i], _gpuPipelineDisplayFrameMs, depth: 0, this);

            ImGui.EndTable();
        }

        ImGui.End();
    }

    public void DrawThreadAllocationsPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("Thread Allocations", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("Thread Allocations"))
            {
                ImGui.End();
                return;
            }
        }

        var allocs = _source.LatestAllocations;
        if (allocs is null)
        {
            ImGui.TextDisabled("Waiting for allocation data...");
            ImGui.End();
            return;
        }

        ImGui.TextDisabled("Uses GC.GetAllocatedBytesForCurrentThread() deltas per tick/frame.");

        if (ImGui.BeginTable("ThreadAllocations", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Thread");
            ImGui.TableSetupColumn("Last (KB)");
            ImGui.TableSetupColumn("Avg (KB)");
            ImGui.TableSetupColumn("Max (KB)");
            ImGui.TableSetupColumn("Samples");
            ImGui.TableHeadersRow();

            DrawAllocRow("Render", allocs.Render);
            DrawAllocRow("Collect+Swap", allocs.CollectSwap);
            DrawAllocRow("Update", allocs.Update);
            DrawAllocRow("FixedUpdate", allocs.FixedUpdate);

            ImGui.EndTable();
        }

        ImGui.End();
    }

    public void DrawComponentTimingsPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("Component Timings", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("Component Timings"))
            {
                ImGui.End();
                return;
            }
        }

        var frame = _source.LatestFrame;
        var components = frame?.ComponentTimings;
        if (components is null || components.Length == 0)
        {
            ImGui.TextDisabled("Waiting for component timing samples...");
            ImGui.End();
            return;
        }

        float totalMs = 0.0f;
        for (int i = 0; i < components.Length; i++)
            totalMs += components[i].ElapsedMs;

        ImGui.Text($"Captured at {frame!.FrameTime:F3}s");
        ImGui.Text($"Components with update work: {components.Length:N0}");
        ImGui.Text($"Total measured component tick time: {totalMs:F3} ms");
        ImGui.TextDisabled("Measures variable update tick delegates owned by scene components. Entries are pre-sorted by total time.");

        float rowH = ImGui.GetTextLineHeightWithSpacing();
        float estH = MathF.Min(18, components.Length) * rowH + rowH * 2;

        if (ImGui.BeginTable("ComponentTimings", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
            new Vector2(-1f, estH)))
        {
            ImGui.TableSetupColumn("Component", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Scene Node", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("Time (ms)", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Avg (us)", ImGuiTableColumnFlags.WidthFixed, 75f);
            ImGui.TableSetupColumn("Groups", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            foreach (var component in components)
            {
                float averageUs = component.CallCount > 0
                    ? component.ElapsedMs * 1000.0f / component.CallCount
                    : 0.0f;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(component.ComponentName);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(component.ComponentId.ToString());
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(component.SceneNodeName);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(component.ComponentType);
                ImGui.TableSetColumnIndex(3); ImGui.Text($"{component.ElapsedMs:F3}");
                ImGui.TableSetColumnIndex(4); ImGui.Text(component.CallCount.ToString());
                ImGui.TableSetColumnIndex(5); ImGui.Text($"{averageUs:F1}");
                ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(FormatTickGroupMask(component.TickGroupMask));
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    public void DrawBvhMetricsPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("BVH Metrics", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("BVH Metrics"))
            {
                ImGui.End();
                return;
            }
        }

        var bvh = _source.LatestBvhMetrics;
        if (bvh is null)
        {
            ImGui.TextDisabled("Waiting for BVH metrics...");
            ImGui.End();
            return;
        }

        ImGui.Text($"Build: {bvh.BuildCount:N0} items, {bvh.BuildMilliseconds:F3} ms");
        ImGui.Text($"Refit: {bvh.RefitCount:N0} nodes, {bvh.RefitMilliseconds:F3} ms");
        ImGui.Text($"Cull: {bvh.CullCount:N0} entries, {bvh.CullMilliseconds:F3} ms");
        ImGui.Text($"Raycasts: {bvh.RaycastCount:N0} rays, {bvh.RaycastMilliseconds:F3} ms");

        ImGui.End();
    }

    public void DrawJobSystemPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("Job System", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("Job System"))
            {
                ImGui.End();
                return;
            }
        }

        var jobs = _source.LatestJobStats;
        if (jobs is null)
        {
            ImGui.TextDisabled("Waiting for job system stats...");
            ImGui.End();
            return;
        }

        ImGui.Text($"Workers: {jobs.WorkerCount}");
        ImGui.Text($"Queue: {(jobs.IsQueueBounded ? "bounded" : "unbounded")}, capacity {jobs.QueueCapacity}, in-use {jobs.QueueSlotsInUse}, available {jobs.QueueSlotsAvailable}");

        if (ImGui.BeginTable("JobSystemStats", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Priority");
            ImGui.TableSetupColumn("Any");
            ImGui.TableSetupColumn("Main");
            ImGui.TableSetupColumn("Collect");
            ImGui.TableSetupColumn("Avg Wait (ms)");
            ImGui.TableSetupColumn("Starving?");
            ImGui.TableHeadersRow();

            if (jobs.Priorities is not null)
            {
                foreach (var entry in jobs.Priorities)
                {
                    if (entry is null) continue;
                    bool starving = entry.AvgWaitMs >= 2000.0;
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.Text(entry.PriorityName ?? $"P{entry.Priority}");
                    ImGui.TableSetColumnIndex(1); ImGui.Text(entry.QueuedAny.ToString());
                    ImGui.TableSetColumnIndex(2); ImGui.Text(entry.QueuedMain.ToString());
                    ImGui.TableSetColumnIndex(3); ImGui.Text(entry.QueuedCollect.ToString());
                    ImGui.TableSetColumnIndex(4); ImGui.Text($"{entry.AvgWaitMs:F1}");
                    ImGui.TableSetColumnIndex(5); ImGui.TextDisabled(starving ? "yes" : "no");
                }
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    public void DrawMainThreadInvokesPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("Main Thread Invokes", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("Main Thread Invokes"))
            {
                ImGui.End();
                return;
            }
        }

        var invokes = _source.LatestMainThreadInvokes;
        if (invokes is null || invokes.Entries is null)
        {
            ImGui.TextDisabled("Waiting for invoke data...");
            ImGui.End();
            return;
        }

        ImGui.Text($"Total invokes: {invokes.Entries.Length:N0}");

        if (invokes.Entries.Length == 0)
        {
            ImGui.TextDisabled("No main thread invokes recorded yet.");
            ImGui.End();
            return;
        }

        float rowH = ImGui.GetTextLineHeightWithSpacing();
        float estH = MathF.Min(20, invokes.Entries.Length) * rowH + rowH * 2;

        if (ImGui.BeginTable("MainThreadInvokes", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
            new Vector2(-1f, estH)))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Timestamp", ImGuiTableColumnFlags.WidthFixed, 185f);
            ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Thread", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var entry in invokes.Entries)
            {
                if (entry is null) continue;
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text(entry.Sequence.ToString());
                ImGui.TableSetColumnIndex(1);
                var ts = new DateTime(entry.TimestampTicks, DateTimeKind.Utc).ToLocalTime();
                ImGui.Text(ts.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                ImGui.TableSetColumnIndex(2); ImGui.Text(entry.Mode ?? "?");
                ImGui.TableSetColumnIndex(3); ImGui.Text(entry.CallerThreadId.ToString());
                ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(entry.Reason ?? "?");
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    public void DrawConnectionInfoPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("Connection Info", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("Connection Info"))
            {
                ImGui.End();
                return;
            }
        }

        bool connected = _source.IsConnected;

        // ── Connection status ──
        if (connected)
        {
            ImGui.TextColored(new Vector4(0.2f, 0.9f, 0.2f, 1f), "Status: Connected");
        }
        else
        {
            double secs = _source.SecondsSinceLastHeartbeat;
            if (secs == double.MaxValue)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.2f, 1f), "Status: Waiting for data...");
                ImGui.TextWrapped("No engine heartbeat received yet. Ensure the engine is running with XRE_PROFILER_ENABLED=1.");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), "Status: Disconnected");
                ImGui.Text($"Last heartbeat: {secs:F1}s ago");
                ImGui.TextWrapped("The engine connection was lost. Waiting for reconnection...");
            }
        }

        ImGui.Separator();
        ImGui.Text($"Packets Received: {_source.PacketsReceived:N0}");
        ImGui.Text($"Bytes Received:   {FormatBytes(_source.BytesReceived)}");
        ImGui.Text($"Errors:           {_source.ErrorsCount:N0}");

        var hb = _source.LatestHeartbeat;
        if (hb is not null)
        {
            ImGui.Separator();
            ImGui.Text($"Engine: {hb.ProcessName}");
            ImGui.Text($"PID:    {hb.ProcessId}");
            ImGui.Text($"Uptime: {hb.UptimeMs / 1000.0:F0} s");
        }

        // ── Source selector (multi-instance) ──
        var sources = _source.GetKnownSources();
        if (sources.Count > 0)
        {
            ImGui.Separator();
            if (sources.Count > 1)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.2f, 1f),
                    $"\u26a0 {sources.Count} engine instances detected");
                ImGui.TextWrapped(
                    "Multiple engines are sending telemetry to the same port. " +
                    "Data from all instances is interleaved. " +
                    "Use different ports to isolate each instance.");
            }

            if (ImGui.CollapsingHeader($"Known Sources ({sources.Count})###Sources"))
            {
                if (ImGui.BeginTable("SourcesTable", 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("PID", ImGuiTableColumnFlags.None, 60);
                    ImGui.TableSetupColumn("Process", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Uptime", ImGuiTableColumnFlags.None, 80);
                    ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.None, 80);
                    ImGui.TableHeadersRow();

                    var now = DateTime.UtcNow;
                    foreach (var src in sources)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn(); ImGui.Text(src.ProcessId.ToString());
                        ImGui.TableNextColumn(); ImGui.Text(src.ProcessName);
                        ImGui.TableNextColumn(); ImGui.Text($"{src.UptimeMs / 1000.0:F0}s");
                        ImGui.TableNextColumn();
                        double age = (now - src.LastSeenUtc).TotalSeconds;
                        if (age < 3.0)
                            ImGui.TextColored(new Vector4(0.2f, 0.9f, 0.2f, 1f), "active");
                        else
                            ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.2f, 1f), $"{age:F0}s ago");
                    }

                    ImGui.EndTable();
                }
            }
        }

        ImGui.End();
    }

    /// <summary>Draws the shared profiler settings controls (no Begin/End). Embed in any host window.</summary>
    public void DrawSettingsContent()
    {
        ImGui.Checkbox("Pause", ref _paused);
        ImGui.SameLine();
        ImGui.Checkbox("Sort by Time", ref _sortByTime);

        ImGui.SetNextItemWidth(100);
        ImGui.SliderFloat("Smoothing", ref _smoothingAlpha, 0.0f, 0.95f, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.DragFloat("Update (s)", ref _updateIntervalSeconds, 0.05f, 0.0f, 2.0f))
            _updateIntervalSeconds = Math.Clamp(_updateIntervalSeconds, 0.0f, 2.0f);
        ImGui.SameLine();
        ImGui.TextDisabled(_updateIntervalSeconds <= 0.0f ? "every render" : "buffered");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("Persist (s)", ref _persistenceSeconds, 0.1f, 0.5f, 10.0f);

        ImGui.SetNextItemWidth(110);
        ImGui.DragInt("Graph Samples", ref _graphSampleCount, 1.0f, 30, 720);
        _graphSampleCount = Math.Clamp(_graphSampleCount, 30, 720);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.DragFloat("Min ms", ref _rootHierarchyMinMs, 0.05f, 0.0f, 1000.0f);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.DragFloat("Max ms", ref _rootHierarchyMaxMs, 0.05f, 0.0f, 1000.0f);

        ImGui.SeparatorText("CPU Timing Graphs");
        ImGui.Checkbox("Show CPU Raw ms", ref _showCpuTimingRawMsLine);
        ImGui.SameLine();
        ImGui.Checkbox("Show CPU Display ms", ref _showCpuTimingSmoothedMsLine);
        ImGui.SameLine();
        ImGui.Checkbox("Interpolate CPU", ref _interpolateCpuTimingGraphs);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        int cpuMode = (int)_cpuTimingDisplayMode;
        if (ImGui.Combo("CPU Display##cpuDisplayMode", ref cpuMode, TimingDisplayModeLabels, TimingDisplayModeLabels.Length))
            _cpuTimingDisplayMode = (ProfilerTimingDisplayMode)cpuMode;

        ImGui.SeparatorText("GPU Timing Graphs");
        ImGui.Checkbox("Show GPU Raw ms", ref _showGpuTimingRawMsLine);
        ImGui.SameLine();
        ImGui.Checkbox("Show GPU Display ms", ref _showGpuTimingSmoothedMsLine);
        ImGui.SameLine();
        ImGui.Checkbox("Interpolate GPU", ref _interpolateGpuTimingGraphs);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        int gpuMode = (int)_gpuTimingDisplayMode;
        if (ImGui.Combo("GPU Display##gpuDisplayMode", ref gpuMode, TimingDisplayModeLabels, TimingDisplayModeLabels.Length))
            _gpuTimingDisplayMode = (ProfilerTimingDisplayMode)gpuMode;
    }

    /// <summary>Draws the shared profiler settings as a standalone ImGui window.</summary>
    public void DrawSettingsPanel(ref bool open, bool allowClose = true)
    {
        if (!open)
            return;
        if (allowClose)
        {
            if (!ImGui.Begin("Settings", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("Settings"))
            {
                ImGui.End();
                return;
            }
        }

        DrawSettingsContent();
        ImGui.End();
    }

    public void DrawCorePanels(
        ref bool showSettings,
        ref bool showProfilerTree,
        ref bool showFpsDropSpikes,
        ref bool showRenderStats,
        ref bool showGpuPipeline,
        ref bool showThreadAllocations,
        ref bool showComponentTimings,
        ref bool showBvhMetrics,
        ref bool showJobSystem,
        ref bool showMainThreadInvokes,
        bool allowClose = true)
    {
        if (showSettings) DrawSettingsPanel(ref showSettings, allowClose);
        if (showProfilerTree) DrawProfilerTreePanel(ref showProfilerTree, allowClose);
        if (showFpsDropSpikes) DrawFpsDropSpikesPanel(ref showFpsDropSpikes, allowClose);
        if (showRenderStats) DrawRenderStatsPanel(ref showRenderStats, allowClose);
        if (showGpuPipeline) DrawGpuPipelinePanel(ref showGpuPipeline, allowClose);
        if (showThreadAllocations) DrawThreadAllocationsPanel(ref showThreadAllocations, allowClose);
        if (showComponentTimings) DrawComponentTimingsPanel(ref showComponentTimings, allowClose);
        if (showBvhMetrics) DrawBvhMetricsPanel(ref showBvhMetrics, allowClose);
        if (showJobSystem) DrawJobSystemPanel(ref showJobSystem, allowClose);
        if (showMainThreadInvokes) DrawMainThreadInvokesPanel(ref showMainThreadInvokes, allowClose);
    }

    public void DrawCorePanelsWithConnectionInfo(
        ref bool showSettings,
        ref bool showProfilerTree,
        ref bool showFpsDropSpikes,
        ref bool showRenderStats,
        ref bool showGpuPipeline,
        ref bool showThreadAllocations,
        ref bool showComponentTimings,
        ref bool showBvhMetrics,
        ref bool showJobSystem,
        ref bool showMainThreadInvokes,
        ref bool showConnectionInfo,
        bool allowClose = true)
    {
        DrawCorePanels(
            ref showSettings,
            ref showProfilerTree,
            ref showFpsDropSpikes,
            ref showRenderStats,
            ref showGpuPipeline,
            ref showThreadAllocations,
            ref showComponentTimings,
            ref showBvhMetrics,
            ref showJobSystem,
            ref showMainThreadInvokes,
            allowClose);

        if (showConnectionInfo) DrawConnectionInfoPanel(ref showConnectionInfo, allowClose);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Processing logic
    // ═══════════════════════════════════════════════════════════════

    private void UpdateWorstFrameStatistics(ProfilerFramePacket snapshot, DateTime now)
    {
        if (_worstFrameWindowStart == DateTime.MinValue)
            _worstFrameWindowStart = now;

        float currentFrameMs = 0f;
        if (snapshot.Threads is not null)
        {
            foreach (var t in snapshot.Threads)
            {
                if (t is not null && t.TotalTimeMs > currentFrameMs)
                    currentFrameMs = t.TotalTimeMs;
            }
        }

        if (_worstFrameWindowSnapshot is null || currentFrameMs > _worstFrameWindowMaxMs)
        {
            _worstFrameWindowMaxMs = currentFrameMs;
            _worstFrameWindowSnapshot = snapshot;
        }

        if (now - _worstFrameWindowStart >= WorstFrameWindowDuration)
        {
            _worstFrameDisplayMs = _worstFrameWindowMaxMs;
            _worstFrameDisplaySnapshot = _worstFrameWindowSnapshot;
            _worstFrameWindowMaxMs = currentFrameMs;
            _worstFrameWindowSnapshot = snapshot;
            _worstFrameWindowStart = now;
        }
    }

    private ProfilerFramePacket GetSnapshotForHierarchy(ProfilerFramePacket current, out float frameMs, out bool usingWorst)
    {
        if (_worstFrameDisplaySnapshot is not null)
        {
            usingWorst = true;
            frameMs = _worstFrameDisplayMs;
            return _worstFrameDisplaySnapshot;
        }

        usingWorst = false;
        float maxMs = 0f;
        if (current.Threads is not null)
        {
            foreach (var t in current.Threads)
            {
                if (t is not null && t.TotalTimeMs > maxMs)
                    maxMs = t.TotalTimeMs;
            }
        }
        frameMs = maxMs;
        return current;
    }

    private void UpdateThreadCache(ProfilerThreadData[] threads, DateTime now)
    {
        foreach (var thread in threads)
        {
            if (thread is null) continue;
            if (!_threadCache.TryGetValue(thread.ThreadId, out var entry))
            {
                entry = new ProfilerThreadCacheEntry { ThreadId = thread.ThreadId };
                _threadCache[thread.ThreadId] = entry;
            }
            entry.LastSeen = now;
            entry.IsStale = false;
        }

        RefreshThreadCacheState(now);
    }

    private void RefreshThreadCacheState(DateTime now)
    {
        foreach (var entry in _threadCache.Values)
        {
            if (now - entry.LastSeen > ThreadStaleThreshold)
                entry.IsStale = true;
        }

        _threadKeysToRemove.Clear();
        foreach (var kvp in _threadCache)
        {
            if (now - kvp.Value.LastSeen > ThreadCacheTimeout)
                _threadKeysToRemove.Add(kvp.Key);
        }
        foreach (var key in _threadKeysToRemove)
            _threadCache.Remove(key);
    }

    private void UpdateRootMethodCache(ProfilerFramePacket frame, Dictionary<int, float[]> history, DateTime now)
    {
        foreach (var entry in _rootMethodCache.Values)
        {
            entry.RootNodes.Clear();
            entry.ThreadIds.Clear();
            entry.TotalTimeMs = 0;
            ResetAggregatedChildrenStats(entry.Children);
        }

        if (frame.Threads is not null)
        {
            foreach (var thread in frame.Threads)
            {
                if (thread?.RootNodes is null) continue;
                foreach (var rootNode in thread.RootNodes)
                {
                    if (rootNode is null) continue;
                    if (!_rootMethodCache.TryGetValue(rootNode.Name, out var agg))
                    {
                        agg = new ProfilerRootMethodAggregate { Name = rootNode.Name };
                        _rootMethodCache[rootNode.Name] = agg;
                    }
                    agg.RootNodes.Add(rootNode);
                    agg.ThreadIds.Add(thread.ThreadId);
                    agg.TotalTimeMs += rootNode.ElapsedMs;
                    agg.SeenThisUpdate = true;
                    agg.LastSeen = now;
                    UpdateAggregatedChildrenRecursive(rootNode.Children, agg.Children, now);
                }
            }
        }

        foreach (var entry in _rootMethodCache.Values)
        {
            entry.AccumulatedMaxTotalTimeMs = Math.Max(entry.AccumulatedMaxTotalTimeMs, entry.TotalTimeMs);
            entry.AccumulatedMaxThreadCount = Math.Max(entry.AccumulatedMaxThreadCount, entry.ThreadIds.Count);
            entry.AccumulatedMaxRootNodeCount = Math.Max(entry.AccumulatedMaxRootNodeCount, entry.RootNodes.Count);
            UpdateAggregatedChildrenMaxRecursive(entry.Children);
        }

        foreach (var agg in _rootMethodCache.Values)
        {
            if (agg.ThreadIds.Count == 0) continue;
            int maxLen = 0;
            foreach (var tid in agg.ThreadIds)
            {
                if (history.TryGetValue(tid, out var s))
                    maxLen = Math.Max(maxLen, s.Length);
            }
            if (maxLen > 0)
            {
                if (agg.Samples.Length != maxLen)
                    agg.Samples = new float[maxLen];
                else
                    Array.Clear(agg.Samples, 0, agg.Samples.Length);

                foreach (var tid in agg.ThreadIds)
                {
                    if (history.TryGetValue(tid, out var s))
                    {
                        for (int i = 0; i < s.Length && i < maxLen; i++)
                            agg.Samples[i] += s[i];
                    }
                }
            }
        }
    }

    private void PruneRootMethodCache(DateTime now)
    {
        foreach (var entry in _rootMethodCache.Values)
            PruneAggregatedChildren(entry.Children, now);

        if (_paused)
            return;

        _rootKeysToRemove.Clear();
        var threshold = TimeSpan.FromSeconds(_persistenceSeconds);
        foreach (var kvp in _rootMethodCache)
        {
            if (now - kvp.Value.LastSeen > threshold)
                _rootKeysToRemove.Add(kvp.Key);
        }
        foreach (var key in _rootKeysToRemove)
            _rootMethodCache.Remove(key);
    }

    private void UpdateDisplayValues()
    {
        foreach (var entry in _rootMethodCache.Values)
        {
            RecordRootMethodHistorySample(entry);

            if (entry.SeenThisUpdate)
            {
                entry.DisplayTotalTimeMs = ApplyDisplaySmoothing(entry.DisplayTotalTimeMs, entry.AccumulatedMaxTotalTimeMs);
                entry.DisplayThreadCount = entry.AccumulatedMaxThreadCount;
                entry.DisplayRootNodeCount = entry.AccumulatedMaxRootNodeCount;
            }
            entry.AccumulatedMaxTotalTimeMs = 0;
            entry.AccumulatedMaxThreadCount = 0;
            entry.AccumulatedMaxRootNodeCount = 0;
            entry.SeenThisUpdate = false;
            UpdateAggregatedChildrenDisplayRecursive(entry.Children);
            UpdateGraphSeries(entry);
        }
    }

    private static void RecordRootMethodHistorySample(ProfilerRootMethodAggregate entry)
    {
        int callCount = entry.AccumulatedMaxRootNodeCount;
        if (callCount > 0)
        {
            float avgCallMs = entry.AccumulatedMaxTotalTimeMs / callCount;
            entry.PerCallMsHistory.Enqueue(avgCallMs);
            entry.CallCountHistory.Enqueue(callCount);
        }
        else
        {
            entry.PerCallMsHistory.Enqueue(0f);
            entry.CallCountHistory.Enqueue(0);
        }

        while (entry.PerCallMsHistory.Count > ProfilerRootMethodAggregate.MethodHistoryCapacity)
            entry.PerCallMsHistory.Dequeue();
        while (entry.CallCountHistory.Count > ProfilerRootMethodAggregate.MethodHistoryCapacity)
            entry.CallCountHistory.Dequeue();
    }

    private float ApplyDisplaySmoothing(float previousMs, float incomingMs)
    {
        if (_smoothingAlpha <= 0.001f)
            return incomingMs;
        if (previousMs <= 0.0001f)
            return incomingMs;
        // _smoothingAlpha is smoothing strength: 0 = none, 0.95 = heavy
        float emaWeight = 1.0f - _smoothingAlpha;
        return previousMs + (incomingMs - previousMs) * emaWeight;
    }

    private void UpdateGraphSeries(ProfilerRootMethodAggregate entry)
    {
        // Use per-method per-call-average history when available (accurate per-call Hz).
        // Falls back to thread-based Samples for backward compat with old data sources.
        int historyCount = entry.PerCallMsHistory.Count;
        if (historyCount > 0)
        {
            int sampleCount = Math.Min(Math.Clamp(_graphSampleCount, 30, 720), historyCount);
            int skip = historyCount - sampleCount;

            if (entry.RawHzSamples.Length != sampleCount)
                entry.RawHzSamples = new float[sampleCount];
            if (entry.DisplayHzSamples.Length != sampleCount)
                entry.DisplayHzSamples = new float[sampleCount];

            int idx = 0;
            float displayMs = 0.0f;
            bool hasDisplayMs = false;

            foreach (float avgMs in entry.PerCallMsHistory)
            {
                if (idx < skip) { idx++; continue; }
                int i = idx - skip;
                float incomingMs = Math.Max(avgMs, 0.0001f);
                entry.RawHzSamples[i] = 1000.0f / incomingMs;
                displayMs = !hasDisplayMs ? incomingMs : ApplyDisplaySmoothing(displayMs, incomingMs);
                hasDisplayMs = true;
                entry.DisplayHzSamples[i] = 1000.0f / Math.Max(displayMs, 0.0001f);
                idx++;
            }
            return;
        }

        // Fallback: thread-based Samples (sum of all roots on thread per snapshot)
        var msSamples = entry.Samples;
        if (msSamples.Length == 0)
        {
            entry.RawHzSamples = [];
            entry.DisplayHzSamples = [];
            return;
        }

        int fallbackCount = Math.Min(Math.Clamp(_graphSampleCount, 30, 720), msSamples.Length);
        int start = msSamples.Length - fallbackCount;

        if (entry.RawHzSamples.Length != fallbackCount)
            entry.RawHzSamples = new float[fallbackCount];
        if (entry.DisplayHzSamples.Length != fallbackCount)
            entry.DisplayHzSamples = new float[fallbackCount];

        float fbDisplayMs = 0.0f;
        bool fbHasDisplayMs = false;

        for (int i = 0; i < fallbackCount; i++)
        {
            float incomingMs = Math.Max(msSamples[start + i], 0.0001f);
            entry.RawHzSamples[i] = 1000.0f / incomingMs;
            fbDisplayMs = !fbHasDisplayMs ? incomingMs : ApplyDisplaySmoothing(fbDisplayMs, incomingMs);
            fbHasDisplayMs = true;
            entry.DisplayHzSamples[i] = 1000.0f / Math.Max(fbDisplayMs, 0.0001f);
        }
    }

    private void DrawDualHzPlot(
        string id,
        string label,
        float[] rawHzSamples,
        float[] displayHzSamples,
        int sampleCount,
        float minHz,
        float maxHz,
        Vector2 requestedSize,
        bool showRaw,
        bool showDisplay,
        bool interpolate)
    {
        if (sampleCount <= 0)
            return;

        float[] resolvedDisplaySamples = displayHzSamples;
        TimingGraphInterpolationState? interpolationState = null;
        string interpolationKey = $"RootHz::{label}";
        if (showDisplay)
        {
            resolvedDisplaySamples = ResolveTimingDisplaySamples(
                interpolationKey,
                displayHzSamples,
                sampleCount,
                interpolate,
                _cpuRootMethodHzInterpolationStates,
                _rootMethodHzInterpolatedScratch);
            _cpuRootMethodHzInterpolationStates.TryGetValue(interpolationKey, out interpolationState);
        }

        DrawMultiHistoryPlot(
            id,
            rawHzSamples,
            showRaw,
            resolvedDisplaySamples,
            showDisplay,
            sampleCount,
            requestedSize,
            minHz,
            maxHz,
            new Vector4(0.55f, 0.75f, 1.00f, 1.00f),
            new Vector4(1.00f, 0.70f, 0.25f, 1.00f),
            new Vector4(1.00f, 0.70f, 0.25f, 0.12f),
            interpolationState,
            showFrameBudgetGuides: false);

        DrawHzPlotHoverTooltip(rawHzSamples, resolvedDisplaySamples, sampleCount, showRaw, showDisplay);
    }

    private void DrawHistoryPlot(
        string id,
        float[] samples,
        int sampleCount,
        Vector2 requestedSize,
        float minY,
        float maxY,
        Vector4 lineColor,
        Vector4 fillColor,
        bool showFrameBudgetGuides)
    {
        if (sampleCount <= 0)
            return;

        Vector2 size = requestedSize;
        if (size.X <= 0f)
            size.X = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        size.Y = MathF.Max(1f, size.Y);

        ImGui.InvisibleButton(id, size);

        Vector2 pMin = ImGui.GetItemRectMin();
        Vector2 pMax = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        const float padding = 4.0f;
        Vector2 plotMin = new(pMin.X + padding, pMin.Y + padding);
        Vector2 plotMax = new(pMax.X - padding, pMax.Y - padding);

        drawList.AddRectFilled(pMin, pMax, ImGui.GetColorU32(ImGuiCol.FrameBg), 3.0f);
        drawList.AddRect(pMin, pMax, ImGui.GetColorU32(ImGuiCol.Border), 3.0f);

        DrawHistoryPlotGuides(drawList, plotMin, plotMax, minY, maxY, showFrameBudgetGuides);
        DrawHistorySeriesFill(drawList, samples, sampleCount, plotMin, plotMax, minY, maxY, fillColor);
        DrawHistorySeriesLine(drawList, samples, sampleCount, plotMin, plotMax, minY, maxY, lineColor, 1.8f);
        DrawHistoryLatestMarker(drawList, samples[sampleCount - 1], plotMin, plotMax, minY, maxY, lineColor);

        if (ImGui.IsItemHovered())
            DrawHistoryHoveredSampleMarker(drawList, samples, sampleCount, plotMin, plotMax, minY, maxY, lineColor);
    }

    private static void DrawHistoryPlotGuides(ImDrawListPtr drawList, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, bool showFrameBudgetGuides)
    {
        float width = plotMax.X - plotMin.X;
        float height = plotMax.Y - plotMin.Y;
        if (width <= 0f || height <= 0f)
            return;

        uint gridColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.08f));
        for (int i = 1; i < 4; i++)
        {
            float y = plotMin.Y + (height * i / 4f);
            drawList.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), gridColor, 1.0f);
        }

        if (!showFrameBudgetGuides)
            return;

        DrawHistoryGuideLine(drawList, plotMin, plotMax, minY, maxY, 8.3333f, "120", new Vector4(0.55f, 0.92f, 1.00f, 0.80f));
        DrawHistoryGuideLine(drawList, plotMin, plotMax, minY, maxY, 11.1111f, "90", new Vector4(0.42f, 0.88f, 0.88f, 0.80f));
        DrawHistoryGuideLine(drawList, plotMin, plotMax, minY, maxY, 16.6667f, "60", new Vector4(0.30f, 0.85f, 0.45f, 0.80f));
        DrawHistoryGuideLine(drawList, plotMin, plotMax, minY, maxY, 33.3333f, "30", new Vector4(0.95f, 0.55f, 0.20f, 0.75f));
    }

    private static void DrawHistoryGuideLine(ImDrawListPtr drawList, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, float value, string label, Vector4 color)
    {
        if (value < minY || value > maxY)
            return;

        float t = NormalizePlotValue(value, minY, maxY);
        float y = plotMax.Y - ((plotMax.Y - plotMin.Y) * t);
        uint colorU32 = ImGui.GetColorU32(color);
        drawList.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), colorU32, 1.0f);

        Vector2 textSize = ImGui.CalcTextSize(label);
        float labelX = plotMax.X - textSize.X - 2f;
        float labelY = y - textSize.Y - 1f;
        if (labelY < plotMin.Y)
            labelY = y + 1f;
        drawList.AddText(new Vector2(labelX, labelY), colorU32, label);
    }

    private static void DrawHistorySeriesFill(ImDrawListPtr drawList, float[] samples, int sampleCount, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, Vector4 color)
    {
        if (sampleCount < 2)
            return;

        float width = plotMax.X - plotMin.X;
        float height = plotMax.Y - plotMin.Y;
        if (width <= 0f || height <= 0f)
            return;

        uint colorU32 = ImGui.GetColorU32(color);
        for (int i = 1; i < sampleCount; i++)
        {
            Vector2 previous = GetPlotPoint(samples, i - 1, sampleCount, plotMin, plotMax, minY, maxY);
            Vector2 current = GetPlotPoint(samples, i, sampleCount, plotMin, plotMax, minY, maxY);
            drawList.AddQuadFilled(
                new Vector2(previous.X, plotMax.Y),
                previous,
                current,
                new Vector2(current.X, plotMax.Y),
                colorU32);
        }
    }

    private static void DrawHistorySeriesLine(ImDrawListPtr drawList, float[] samples, int sampleCount, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, Vector4 color, float thickness)
    {
        if (sampleCount < 2)
            return;

        uint colorU32 = ImGui.GetColorU32(color);
        Vector2 previous = GetPlotPoint(samples, 0, sampleCount, plotMin, plotMax, minY, maxY);
        for (int i = 1; i < sampleCount; i++)
        {
            Vector2 current = GetPlotPoint(samples, i, sampleCount, plotMin, plotMax, minY, maxY);
            drawList.AddLine(previous, current, colorU32, thickness);
            previous = current;
        }
    }

    private static void DrawScrollOffsetHistorySeriesFill(ImDrawListPtr drawList, float[] samples, int sampleCount, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, Vector4 color, float xOffset)
    {
        if (sampleCount < 2)
            return;

        uint colorU32 = ImGui.GetColorU32(color);
        Vector2 previous = GetScrollOffsetPlotPoint(samples, 0, sampleCount, plotMin, plotMax, minY, maxY, xOffset);

        // Extend the oldest sample to the left edge so there's no gap.
        if (previous.X > plotMin.X + 0.5f)
        {
            Vector2 leftEdge = new(plotMin.X, previous.Y);
            drawList.AddQuadFilled(
                new Vector2(leftEdge.X, plotMax.Y),
                leftEdge,
                previous,
                new Vector2(previous.X, plotMax.Y),
                colorU32);
        }

        for (int i = 1; i < sampleCount; i++)
        {
            Vector2 current = GetScrollOffsetPlotPoint(samples, i, sampleCount, plotMin, plotMax, minY, maxY, xOffset);
            drawList.AddQuadFilled(
                new Vector2(previous.X, plotMax.Y),
                previous,
                current,
                new Vector2(current.X, plotMax.Y),
                colorU32);
            previous = current;
        }
    }

    private static void DrawScrollOffsetHistorySeriesLine(ImDrawListPtr drawList, float[] samples, int sampleCount, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, Vector4 color, float thickness, float xOffset)
    {
        if (sampleCount < 2)
            return;

        uint colorU32 = ImGui.GetColorU32(color);
        Vector2 previous = GetScrollOffsetPlotPoint(samples, 0, sampleCount, plotMin, plotMax, minY, maxY, xOffset);

        // Extend the oldest sample to the left edge so there's no gap.
        if (previous.X > plotMin.X + 0.5f)
        {
            Vector2 leftEdge = new(plotMin.X, previous.Y);
            drawList.AddLine(leftEdge, previous, colorU32, thickness);
        }

        for (int i = 1; i < sampleCount; i++)
        {
            Vector2 current = GetScrollOffsetPlotPoint(samples, i, sampleCount, plotMin, plotMax, minY, maxY, xOffset);
            drawList.AddLine(previous, current, colorU32, thickness);
            previous = current;
        }
    }

    private static void DrawScrollOffsetLatestMarker(ImDrawListPtr drawList, float[] samples, int sampleCount, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, Vector4 color, float xOffset)
    {
        Vector2 latestPoint = GetScrollOffsetPlotPoint(samples, sampleCount - 1, sampleCount, plotMin, plotMax, minY, maxY, xOffset);
        drawList.AddCircleFilled(latestPoint, 3.0f, ImGui.GetColorU32(color), 12);
    }

    private static void DrawHistoryLatestMarker(ImDrawListPtr drawList, float latestValue, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, Vector4 color)
    {
        float t = NormalizePlotValue(latestValue, minY, maxY);
        float y = plotMax.Y - ((plotMax.Y - plotMin.Y) * t);
        drawList.AddCircleFilled(new Vector2(plotMax.X, y), 3.0f, ImGui.GetColorU32(color), 12);
    }

    private void DrawHistoryHoveredSampleMarker(ImDrawListPtr drawList, float[] samples, int sampleCount, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, Vector4 color)
    {
        int sampleIndex = GetHoveredHistorySampleIndex(sampleCount);
        if (sampleIndex < 0)
            return;

        Vector2 point = GetPlotPoint(samples, sampleIndex, sampleCount, plotMin, plotMax, minY, maxY);
        uint lineColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.20f));
        drawList.AddLine(new Vector2(point.X, plotMin.Y), new Vector2(point.X, plotMax.Y), lineColor, 1.0f);
        drawList.AddCircleFilled(point, 3.5f, ImGui.GetColorU32(color), 12);
    }

    private int GetHoveredHistorySampleIndex(int sampleCount)
    {
        if (sampleCount <= 0 || !ImGui.IsItemHovered())
            return -1;

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        float width = max.X - min.X;
        if (width <= 1.0f)
            return -1;

        float mouseX = ImGui.GetIO().MousePos.X;
        float t = Math.Clamp((mouseX - min.X) / width, 0.0f, 1.0f);
        return Math.Clamp((int)MathF.Round(t * (sampleCount - 1)), 0, sampleCount - 1);
    }

    private void DrawHistoryPlotHoverTooltip(float[] samples, int sampleCount, string units, int decimals, float latest, float average, float minimum, float maximum)
    {
        if (!ImGui.IsItemHovered())
            return;

        int sampleIndex = GetHoveredHistorySampleIndex(sampleCount);
        if (sampleIndex < 0)
            return;

        float value = samples[sampleIndex];
        string format = $"F{decimals}";

        ImGui.BeginTooltip();
        ImGui.Text($"Sample {sampleIndex + 1}/{sampleCount}");
        ImGui.Separator();
        ImGui.Text($"Value: {value.ToString(format)} {units}");
        ImGui.Text($"Latest: {latest.ToString(format)} {units}");
        ImGui.Text($"Average: {average.ToString(format)} {units}");
        ImGui.Text($"Min/Max: {minimum.ToString(format)} / {maximum.ToString(format)} {units}");
        ImGui.EndTooltip();
    }

    private static Vector2 GetPlotPoint(float[] samples, int index, int sampleCount, Vector2 plotMin, Vector2 plotMax, float minY, float maxY)
    {
        float xT = sampleCount == 1 ? 0f : (float)index / (sampleCount - 1);
        float yT = NormalizePlotValue(samples[index], minY, maxY);
        return new Vector2(
            plotMin.X + ((plotMax.X - plotMin.X) * xT),
            plotMax.Y - ((plotMax.Y - plotMin.Y) * yT));
    }

    /// <summary>
    /// Like <see cref="GetPlotPoint"/> but adds a fractional X offset so the
    /// entire graph can smoothly scroll left between buffered updates.
    /// </summary>
    private static Vector2 GetScrollOffsetPlotPoint(float[] samples, int index, int sampleCount, Vector2 plotMin, Vector2 plotMax, float minY, float maxY, float xOffset)
    {
        float xT = (sampleCount == 1 ? 0f : (float)index / (sampleCount - 1)) + xOffset;
        float yT = NormalizePlotValue(samples[index], minY, maxY);
        return new Vector2(
            plotMin.X + ((plotMax.X - plotMin.X) * xT),
            plotMax.Y - ((plotMax.Y - plotMin.Y) * yT));
    }

    private static float NormalizePlotValue(float value, float minY, float maxY)
    {
        float range = MathF.Max(maxY - minY, 0.001f);
        return Math.Clamp((value - minY) / range, 0.0f, 1.0f);
    }

    private static void DrawHzSeriesLine(ImDrawListPtr drawList, float[] samples, int sampleCount, Vector2 pMin, Vector2 pMax, float minHz, float maxHz, Vector4 color)
    {
        if (sampleCount < 2)
            return;

        float range = MathF.Max(maxHz - minHz, 0.001f);
        float width = pMax.X - pMin.X;
        float height = pMax.Y - pMin.Y;
        float pad = 2.0f;
        uint colorU32 = ImGui.GetColorU32(color);

        Vector2 prev = Vector2.Zero;
        for (int i = 0; i < sampleCount; i++)
        {
            float xT = sampleCount == 1 ? 0f : (float)i / (sampleCount - 1);
            float yT = Math.Clamp((samples[i] - minHz) / range, 0.0f, 1.0f);
            var curr = new Vector2(
                pMin.X + pad + xT * MathF.Max(0.0f, width - (pad * 2.0f)),
                pMax.Y - pad - yT * MathF.Max(0.0f, height - (pad * 2.0f)));

            if (i > 0)
                drawList.AddLine(prev, curr, colorU32, 1.5f);

            prev = curr;
        }
    }

    private void DrawHzPlotHoverTooltip(float[] rawHzSamples, float[] displayHzSamples, int sampleCount, bool showRaw, bool showDisplay)
    {
        if (sampleCount <= 0 || !ImGui.IsItemHovered())
            return;

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        float width = max.X - min.X;
        if (width <= 1.0f)
            return;

        float mouseX = ImGui.GetIO().MousePos.X;
        float t = Math.Clamp((mouseX - min.X) / width, 0.0f, 1.0f);
        int sampleIndex = Math.Clamp((int)MathF.Round(t * (sampleCount - 1)), 0, sampleCount - 1);

        float rawHz = rawHzSamples[sampleIndex];
        float displayHz = displayHzSamples[sampleIndex];
        float rawMs = rawHz > 0.0001f ? 1000.0f / rawHz : 0.0f;
        float displayMs = displayHz > 0.0001f ? 1000.0f / displayHz : 0.0f;
        float deltaHz = displayHz - rawHz;

        ImGui.BeginTooltip();
        ImGui.Text($"Sample {sampleIndex + 1}/{sampleCount}");
        ImGui.Separator();
        if (showRaw)
            ImGui.Text($"Raw: {rawMs:F3} ms ({rawHz:F1} Hz)");
        if (showDisplay)
            ImGui.Text($"Display: {displayMs:F3} ms ({displayHz:F1} Hz)");
        if (showRaw && showDisplay)
            ImGui.Text($"Delta: {deltaHz:+0.0;-0.0;0.0} Hz");
        ImGui.TextDisabled($"Smoothing={_smoothingAlpha:F2}");
        ImGui.EndTooltip();
    }

    private double GetEffectiveUpdateIntervalSeconds()
        => Math.Clamp(_updateIntervalSeconds, 0.0f, 2.0f);

    private string FormatUpdateIntervalLabel()
        => GetEffectiveUpdateIntervalSeconds() <= 0.0 ? "every render" : $"{GetEffectiveUpdateIntervalSeconds():F2}s";

    private double GetStaleWindowSeconds()
    {
        double interval = GetEffectiveUpdateIntervalSeconds();
        return interval > 0.0 ? interval : (1.0 / 60.0);
    }

    private void RebuildCachedRootMethodLists(DateTime nowUtc)
    {
        float minMs = Math.Max(0f, _rootHierarchyMinMs);
        float maxMs = _rootHierarchyMaxMs;
        bool hasMax = maxMs > 0f;
        var staleSec = _persistenceSeconds;
        Dictionary<ProfilerRootMethodAggregate, RootMethodActivitySortKey>? activitySortKeys = null;
        if (!_sortByTime)
            activitySortKeys = new Dictionary<ProfilerRootMethodAggregate, RootMethodActivitySortKey>();

        _cachedGraphList.Clear();
        foreach (var rm in _rootMethodCache.Values)
        {
            bool visible = rm.DisplayTotalTimeMs >= 0.1f || _paused || (nowUtc - rm.LastSeen).TotalSeconds < staleSec;
            bool inRange = rm.DisplayTotalTimeMs >= minMs && (!hasMax || rm.DisplayTotalTimeMs <= maxMs);
            if (visible && inRange)
            {
                _cachedGraphList.Add(rm);
                activitySortKeys?.TryAdd(rm, BuildRootMethodActivitySortKey(rm));
            }
        }
        if (_sortByTime)
            _cachedGraphList.Sort(static (a, b) => b.DisplayTotalTimeMs.CompareTo(a.DisplayTotalTimeMs));
        else
            _cachedGraphList.Sort((a, b) => CompareRootMethodsByActivity(a, b, activitySortKeys!));

        _cachedHierarchyList.Clear();
        foreach (var rm in _rootMethodCache.Values)
        {
            bool visible = rm.DisplayTotalTimeMs >= 0.1f || _paused || (nowUtc - rm.LastSeen).TotalSeconds < staleSec;
            bool inRange = rm.DisplayTotalTimeMs >= minMs && (!hasMax || rm.DisplayTotalTimeMs <= maxMs);
            if (visible && inRange)
            {
                _cachedHierarchyList.Add(rm);
                RebuildCachedChildrenRecursive(rm.Children, rm.DisplayTotalTimeMs, out var sorted, out var untracked, nowUtc, staleSec);
                rm.CachedSortedChildren = sorted;
                rm.CachedUntrackedTime = untracked;
            }
        }

        if (_sortByTime)
            _cachedHierarchyList.Sort(static (a, b) => b.DisplayTotalTimeMs.CompareTo(a.DisplayTotalTimeMs));
        else
            _cachedHierarchyList.Sort((a, b) => CompareRootMethodsByActivity(a, b, activitySortKeys!));
    }

    private static int CompareRootMethodsByActivity(
        ProfilerRootMethodAggregate a,
        ProfilerRootMethodAggregate b,
        Dictionary<ProfilerRootMethodAggregate, RootMethodActivitySortKey> activitySortKeys)
    {
        RootMethodActivitySortKey aKey = activitySortKeys[a];
        RootMethodActivitySortKey bKey = activitySortKeys[b];

        int categoryCompare = aKey.Category.CompareTo(bKey.Category);
        if (categoryCompare != 0)
            return categoryCompare;

        if (aKey.Category == 0)
            return string.CompareOrdinal(a.Name, b.Name);

        int activeRatioCompare = bKey.ActiveRatio.CompareTo(aKey.ActiveRatio);
        if (activeRatioCompare != 0)
            return activeRatioCompare;

        int avgCallsCompare = bKey.AverageCallCount.CompareTo(aKey.AverageCallCount);
        if (avgCallsCompare != 0)
            return avgCallsCompare;

        return string.CompareOrdinal(a.Name, b.Name);
    }

    private static RootMethodActivitySortKey BuildRootMethodActivitySortKey(ProfilerRootMethodAggregate entry)
    {
        int sampleCount = entry.CallCountHistory.Count;
        if (sampleCount <= 0)
            return new RootMethodActivitySortKey(2, 0.0f, 0.0f);

        int activeSamples = 0;
        int totalCalls = 0;
        foreach (int callCount in entry.CallCountHistory)
        {
            if (callCount > 0)
                activeSamples++;

            totalCalls += callCount;
        }

        float activeRatio = (float)activeSamples / sampleCount;
        float averageCallCount = (float)totalCalls / sampleCount;

        int category = activeRatio switch
        {
            >= 0.98f => 0,
            <= 0.50f => 2,
            _ => 1,
        };

        return new RootMethodActivitySortKey(category, activeRatio, averageCallCount);
    }

    private void PushRenderStatsSample(RenderStatsPacket stats)
    {
        int index = _renderStatsHistoryHead;

        _vulkanPipelineBindsHistory[index] = stats.VulkanPipelineBinds;
        _vulkanDescriptorBindsHistory[index] = stats.VulkanDescriptorBinds;

        double pipelineSkipRate = (stats.VulkanPipelineBinds + stats.VulkanPipelineBindSkips) <= 0
            ? 0.0
            : (double)stats.VulkanPipelineBindSkips / (stats.VulkanPipelineBinds + stats.VulkanPipelineBindSkips);
        double descriptorSkipRate = (stats.VulkanDescriptorBinds + stats.VulkanDescriptorBindSkips) <= 0
            ? 0.0
            : (double)stats.VulkanDescriptorBindSkips / (stats.VulkanDescriptorBinds + stats.VulkanDescriptorBindSkips);

        _vulkanPipelineSkipRateHistory[index] = (float)(pipelineSkipRate * 100.0);
        _vulkanDescriptorSkipRateHistory[index] = (float)(descriptorSkipRate * 100.0);
        _vulkanPipelineCacheHitRateHistory[index] = (float)(stats.VulkanPipelineCacheLookupHitRate * 100.0);
        _vulkanFrameTotalMsHistory[index] = (float)stats.VulkanFrameTotalMs;
        _vulkanFrameWaitFenceMsHistory[index] = (float)stats.VulkanFrameWaitFenceMs;
        _vulkanFrameRecordCommandBufferMsHistory[index] = (float)stats.VulkanFrameRecordCommandBufferMs;
        _vulkanFrameGpuCommandBufferMsHistory[index] = (float)stats.VulkanFrameGpuCommandBufferMs;

        _renderStatsHistoryHead = (_renderStatsHistoryHead + 1) % RenderStatsHistorySamples;
        if (_renderStatsHistoryCount < RenderStatsHistorySamples)
            _renderStatsHistoryCount++;

        PushGpuPipelineSample(stats);
    }

    private void PushGpuPipelineSample(RenderStatsPacket stats)
    {
        _gpuPipelineRootSampleSerial++;
        int index = _renderStatsHistoryHead;

        foreach (float[] series in _gpuPipelineRootHistory.Values)
            series[index] = 0f;

        if (!stats.GpuRenderPipelineTimingsReady || stats.GpuRenderPipelineTimingRoots.Length == 0)
            return;

        for (int i = 0; i < stats.GpuRenderPipelineTimingRoots.Length; i++)
        {
            GpuPipelineTimingNodeData root = stats.GpuRenderPipelineTimingRoots[i];
            if (!_gpuPipelineRootHistory.TryGetValue(root.Name, out float[]? series))
            {
                series = new float[RenderStatsHistorySamples];
                _gpuPipelineRootHistory.Add(root.Name, series);
            }

            series[index] = (float)root.ElapsedMs;
            _gpuPipelineRootHistoryLastSeen[root.Name] = _gpuPipelineRootSampleSerial;
        }

        _gpuPipelineRootHistoryStaleKeys.Clear();
        foreach ((string key, int lastSeen) in _gpuPipelineRootHistoryLastSeen)
        {
            if (_gpuPipelineRootSampleSerial - lastSeen <= RenderStatsHistorySamples)
                continue;

            _gpuPipelineRootHistoryStaleKeys.Add(key);
        }

        for (int i = 0; i < _gpuPipelineRootHistoryStaleKeys.Count; i++)
        {
            string key = _gpuPipelineRootHistoryStaleKeys[i];
            _gpuPipelineRootHistory.Remove(key);
            _gpuPipelineRootHistoryLastSeen.Remove(key);
        }
    }

    private int BuildOrderedRenderStatsHistory(float[] ring, float[] destination, bool smooth = true)
    {
        if (_renderStatsHistoryCount <= 0)
            return 0;

        int sampleCount = Math.Min(_renderStatsHistoryCount, _graphSampleCount);
        int start = (_renderStatsHistoryHead - sampleCount + RenderStatsHistorySamples) % RenderStatsHistorySamples;
        float previous = 0f;
        bool hasPrevious = false;
        for (int i = 0; i < sampleCount; i++)
        {
            int idx = (start + i) % RenderStatsHistorySamples;
            float value = ring[idx];
            if (smooth && _smoothingAlpha > 0.001f && hasPrevious)
                value = (previous * _smoothingAlpha) + (value * (1.0f - _smoothingAlpha));

            destination[i] = value;
            previous = value;
            hasPrevious = true;
        }

        return sampleCount;
    }

    private static void DrawGpuPipelineNode(GpuPipelineTimingNodeData node, double frameMs, int depth, ProfilerPanelRenderer owner)
    {
        if (!owner.IsGpuPipelineNodeVisible(node))
            return;

        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(node.Name + depth.ToString());
        ImGuiTreeNodeFlags flags = node.Children.Length == 0 ? ImGuiTreeNodeFlags.Leaf : ImGuiTreeNodeFlags.None;
        bool open = ImGui.TreeNodeEx(node.Name, flags | ImGuiTreeNodeFlags.SpanFullWidth);

        ImGui.TableSetColumnIndex(1);
        float displayMs = owner.GetGpuPipelineRootStat(node.Name, (float)node.ElapsedMs, owner._gpuTimingDisplayMode);
        ImGui.Text($"{displayMs:F3}");

        ImGui.TableSetColumnIndex(2);
        ImGui.Text(node.SampleCount.ToString());

        ImGui.TableSetColumnIndex(3);
        double percent = frameMs <= 0.0001 ? 0.0 : (displayMs / frameMs) * 100.0;
        ImGui.Text($"{percent:F1}%");

        if (open)
        {
            GpuPipelineTimingNodeData[] children = owner.GetOrderedGpuPipelineRoots(node.Children);
            for (int i = 0; i < children.Length; i++)
                DrawGpuPipelineNode(children[i], frameMs, depth + 1, owner);
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private void UpdateGpuPipelineDisplay(RenderStatsPacket? stats)
    {
        if (stats is null)
        {
            _gpuPipelineDisplayEnabled = false;
            _gpuPipelineDisplaySupported = false;
            _gpuPipelineDisplayReady = false;
            _gpuPipelineDisplayBackend = string.Empty;
            _gpuPipelineDisplayStatusMessage = string.Empty;
            _gpuPipelineDisplayFrameMs = 0.0;
            _gpuPipelineDisplayRoots = [];
            return;
        }

        _gpuPipelineDisplayEnabled = stats.GpuRenderPipelineProfilingEnabled;
        _gpuPipelineDisplaySupported = stats.GpuRenderPipelineProfilingSupported;
        _gpuPipelineDisplayReady = stats.GpuRenderPipelineTimingsReady;
        _gpuPipelineDisplayBackend = stats.GpuRenderPipelineBackend ?? string.Empty;
        _gpuPipelineDisplayStatusMessage = stats.GpuRenderPipelineStatusMessage ?? string.Empty;
        _gpuPipelineDisplayFrameMs = stats.GpuRenderPipelineFrameMs;
        _gpuPipelineDisplayRoots = CloneGpuPipelineNodes(stats.GpuRenderPipelineTimingRoots);
    }

    private static GpuPipelineTimingNodeData[] CloneGpuPipelineNodes(GpuPipelineTimingNodeData[] nodes)
    {
        if (nodes.Length == 0)
            return [];

        GpuPipelineTimingNodeData[] copy = new GpuPipelineTimingNodeData[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            GpuPipelineTimingNodeData node = nodes[i];
            copy[i] = new GpuPipelineTimingNodeData
            {
                Name = node.Name,
                ElapsedMs = node.ElapsedMs,
                SampleCount = node.SampleCount,
                Children = CloneGpuPipelineNodes(node.Children),
            };
        }

        return copy;
    }

    private GpuPipelineTimingNodeData[] GetOrderedGpuPipelineRoots(GpuPipelineTimingNodeData[] nodes)
    {
        if (nodes.Length <= 1)
            return nodes;

        GpuPipelineTimingNodeData[] ordered = new GpuPipelineTimingNodeData[nodes.Length];
        Array.Copy(nodes, ordered, nodes.Length);
        if (_sortByTime)
            Array.Sort(ordered, static (a, b) => b.ElapsedMs.CompareTo(a.ElapsedMs));
        else
            Array.Sort(ordered, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        return ordered;
    }

    private bool IsGpuPipelineNodeVisible(GpuPipelineTimingNodeData node)
    {
        float minMs = Math.Max(0f, _rootHierarchyMinMs);
        float maxMs = _rootHierarchyMaxMs;
        bool hasMax = maxMs > 0f;
        double elapsed = node.ElapsedMs;
        return elapsed >= minMs && (!hasMax || elapsed <= maxMs);
    }

    private void DrawRenderStatsHistoryPlot(string label, float[] ring, string units, float minY, float maxY)
    {
        if (_renderStatsHistoryCount <= 0)
            return;

        int sampleCount = BuildOrderedRenderStatsHistory(ring, _renderStatsHistoryRawScratch, smooth: false);
        if (sampleCount <= 0)
            return;

        bool isTimingPlot = string.Equals(units, "ms", StringComparison.Ordinal);
        if (!isTimingPlot)
        {
            DrawTimingHistoryPlot(
                label,
                $"##{label}",
                _renderStatsHistoryRawScratch,
                sampleCount,
                units,
                minY,
                maxY,
                showRawLine: true,
                showSmoothedLine: false,
                interpolate: false,
                interpolationStates: _cpuTimingInterpolationStates,
                smoothedScratch: _renderStatsHistoryScratch,
                interpolatedScratch: _renderStatsHistoryInterpolatedScratch,
                rawColor: new Vector4(0.65f, 0.82f, 1.00f, 1.00f),
                smoothedColor: new Vector4(0.65f, 0.82f, 1.00f, 1.00f),
                smoothedFillColor: new Vector4(0.65f, 0.82f, 1.00f, 0.12f));
            return;
        }

        DrawTimingHistoryPlot(
            label,
            $"##{label}",
            _renderStatsHistoryRawScratch,
            sampleCount,
            units,
            minY,
            maxY,
            showRawLine: _showCpuTimingRawMsLine,
            showSmoothedLine: _showCpuTimingSmoothedMsLine,
            interpolate: _interpolateCpuTimingGraphs,
            interpolationStates: _cpuTimingInterpolationStates,
            smoothedScratch: _renderStatsHistoryScratch,
            interpolatedScratch: _renderStatsHistoryInterpolatedScratch,
            rawColor: new Vector4(0.52f, 0.86f, 1.00f, 0.78f),
            smoothedColor: new Vector4(0.40f, 0.80f, 1.00f, 1.00f),
            smoothedFillColor: new Vector4(0.40f, 0.80f, 1.00f, 0.15f),
            displayMode: _cpuTimingDisplayMode);
    }

    private void DrawTimingHistoryPlot(
        string label,
        string id,
        float[] rawSamples,
        int sampleCount,
        string units,
        float minY,
        float maxY,
        bool showRawLine,
        bool showSmoothedLine,
        bool interpolate,
        Dictionary<string, TimingGraphInterpolationState> interpolationStates,
        float[] smoothedScratch,
        float[] interpolatedScratch,
        Vector4 rawColor,
        Vector4 smoothedColor,
        Vector4 smoothedFillColor,
        ProfilerTimingDisplayMode displayMode = ProfilerTimingDisplayMode.Latest)
    {
        if (sampleCount <= 0)
            return;

        BuildSmoothedHistory(rawSamples, sampleCount, smoothedScratch);
        float[] displaySamples = ResolveTimingDisplaySamples(label, smoothedScratch, sampleCount, interpolate, interpolationStates, interpolatedScratch);
        interpolationStates.TryGetValue(label, out TimingGraphInterpolationState? interpolationState);
        float[] statsSamples = showSmoothedLine ? displaySamples : rawSamples;

        ComputeHistoryStats(
            statsSamples,
            sampleCount,
            minY,
            maxY,
            out float plotMin,
            out float plotMax,
            out float latest,
            out float average,
            out float minimum,
            out float maximum);

        int decimals = string.Equals(units, "ms", StringComparison.Ordinal) ? 3 : 1;
        string format = $"F{decimals}";
        float primary = displayMode switch
        {
            ProfilerTimingDisplayMode.Average => average,
            ProfilerTimingDisplayMode.Worst => maximum,
            _ => latest,
        };
        float fps = primary > 0.0001f && string.Equals(units, "ms", StringComparison.Ordinal) ? 1000.0f / primary : 0.0f;

        if (displayMode == ProfilerTimingDisplayMode.Latest)
            ImGui.Text($"{label}: {latest.ToString(format)} {units}  avg {average.ToString(format)}  min {minimum.ToString(format)}  max {maximum.ToString(format)}{(fps > 0.0f ? $"  ({fps:F1} FPS)" : string.Empty)}");
        else
        {
            string modeTag = displayMode == ProfilerTimingDisplayMode.Average ? "avg" : "worst";
            ImGui.Text($"{label}: {primary.ToString(format)} {units} ({modeTag})  latest {latest.ToString(format)}  avg {average.ToString(format)}  worst {maximum.ToString(format)}{(fps > 0.0f ? $"  ({fps:F1} FPS)" : string.Empty)}");
        }
        DrawMultiHistoryPlot(
            id,
            rawSamples,
            showRawLine,
            displaySamples,
            showSmoothedLine,
            sampleCount,
            new Vector2(-1f, 52f),
            plotMin,
            plotMax,
            rawColor,
            smoothedColor,
            smoothedFillColor,
            interpolationState,
            showFrameBudgetGuides: string.Equals(units, "ms", StringComparison.Ordinal));
        DrawTimingHistoryHoverTooltip(rawSamples, displaySamples, sampleCount, units, decimals, latest, average, minimum, maximum, showRawLine, showSmoothedLine);
    }

    private void BuildSmoothedHistory(float[] rawSamples, int sampleCount, float[] destination)
    {
        if (sampleCount <= 0)
            return;

        float displayValue = rawSamples[0];
        destination[0] = displayValue;
        for (int i = 1; i < sampleCount; i++)
        {
            displayValue = ApplyDisplaySmoothing(displayValue, rawSamples[i]);
            destination[i] = displayValue;
        }
    }

    private float[] ResolveTimingDisplaySamples(
        string label,
        float[] displayBaseSamples,
        int sampleCount,
        bool interpolate,
        Dictionary<string, TimingGraphInterpolationState> interpolationStates,
        float[] interpolatedScratch)
    {
        bool exists = interpolationStates.TryGetValue(label, out TimingGraphInterpolationState? state);
        DateTime nowUtc = DateTime.UtcNow;
        if (!exists || state is null || state.SampleCount != sampleCount)
        {
            state = new TimingGraphInterpolationState(sampleCount);
            interpolationStates[label] = state;
            Array.Copy(displayBaseSamples, state.StartSamples, sampleCount);
            Array.Copy(displayBaseSamples, state.TargetSamples, sampleCount);
            state.LastRefreshUtc = nowUtc;
            state.LastInterpolationT = 1.0f;
            Array.Copy(displayBaseSamples, interpolatedScratch, sampleCount);
            return interpolatedScratch;
        }

        if (!SamplesEqual(displayBaseSamples, state.TargetSamples, sampleCount))
        {
            Array.Copy(state.TargetSamples, state.StartSamples, sampleCount);
            Array.Copy(displayBaseSamples, state.TargetSamples, sampleCount);
            state.LastRefreshUtc = nowUtc;
            state.HasPrevious = true;
        }

        Array.Copy(displayBaseSamples, interpolatedScratch, sampleCount);

        if (!state.HasPrevious)
        {
            state.LastInterpolationT = 1.0f;
            return interpolatedScratch;
        }

        if (!interpolate || GetEffectiveUpdateIntervalSeconds() <= 0.0)
        {
            state.LastInterpolationT = 1.0f;
            return interpolatedScratch;
        }

        float t = (float)Math.Clamp((nowUtc - state.LastRefreshUtc).TotalSeconds / GetEffectiveUpdateIntervalSeconds(), 0.0, 1.0);
        state.LastInterpolationT = t;
        int latestIndex = sampleCount - 1;
        interpolatedScratch[latestIndex] = state.StartSamples[latestIndex] + ((state.TargetSamples[latestIndex] - state.StartSamples[latestIndex]) * t);

        return interpolatedScratch;
    }

    private void DrawMultiHistoryPlot(
        string id,
        float[] rawSamples,
        bool drawRaw,
        float[] displaySamples,
        bool drawDisplay,
        int sampleCount,
        Vector2 requestedSize,
        float minY,
        float maxY,
        Vector4 rawColor,
        Vector4 displayColor,
        Vector4 displayFillColor,
        TimingGraphInterpolationState? displayInterpolationState,
        bool showFrameBudgetGuides)
    {
        if (sampleCount <= 0)
            return;

        Vector2 size = requestedSize;
        if (size.X <= 0f)
            size.X = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        size.Y = MathF.Max(1f, size.Y);

        ImGui.InvisibleButton(id, size);

        Vector2 pMin = ImGui.GetItemRectMin();
        Vector2 pMax = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        const float padding = 4.0f;
        Vector2 plotMin = new(pMin.X + padding, pMin.Y + padding);
        Vector2 plotMax = new(pMax.X - padding, pMax.Y - padding);

        drawList.AddRectFilled(pMin, pMax, ImGui.GetColorU32(ImGuiCol.FrameBg), 3.0f);
        drawList.AddRect(pMin, pMax, ImGui.GetColorU32(ImGuiCol.Border), 3.0f);

        DrawHistoryPlotGuides(drawList, plotMin, plotMax, minY, maxY, showFrameBudgetGuides);

        // Compute a smooth horizontal scroll offset so the whole graph slides
        // left over the update interval instead of jumping.
        bool useScrollOffset = drawDisplay
            && displayInterpolationState is not null
            && displayInterpolationState.HasPrevious
            && displayInterpolationState.LastInterpolationT < 0.999f
            && sampleCount >= 2;
        float xOffset = 0f;
        if (useScrollOffset)
        {
            float step = 1.0f / (sampleCount - 1);
            xOffset = (1.0f - displayInterpolationState!.LastInterpolationT) * step;
        }

        // Clip drawing to the plot area so points scrolling off the edges
        // aren't visible outside the graph.
        if (useScrollOffset)
            drawList.PushClipRect(plotMin, plotMax, true);

        if (drawDisplay)
        {
            if (useScrollOffset)
                DrawScrollOffsetHistorySeriesFill(drawList, displaySamples, sampleCount, plotMin, plotMax, minY, maxY, displayFillColor, xOffset);
            else
                DrawHistorySeriesFill(drawList, displaySamples, sampleCount, plotMin, plotMax, minY, maxY, displayFillColor);
        }
        if (drawRaw)
            DrawHistorySeriesLine(drawList, rawSamples, sampleCount, plotMin, plotMax, minY, maxY, rawColor, 1.2f);
        if (drawDisplay)
        {
            if (useScrollOffset)
            {
                DrawScrollOffsetHistorySeriesLine(drawList, displaySamples, sampleCount, plotMin, plotMax, minY, maxY, displayColor, 1.8f, xOffset);
                DrawScrollOffsetLatestMarker(drawList, displaySamples, sampleCount, plotMin, plotMax, minY, maxY, displayColor, xOffset);
            }
            else
            {
                DrawHistorySeriesLine(drawList, displaySamples, sampleCount, plotMin, plotMax, minY, maxY, displayColor, 1.8f);
                DrawHistoryLatestMarker(drawList, displaySamples[sampleCount - 1], plotMin, plotMax, minY, maxY, displayColor);
            }
        }
        else if (drawRaw)
        {
            DrawHistoryLatestMarker(drawList, rawSamples[sampleCount - 1], plotMin, plotMax, minY, maxY, rawColor);
        }

        if (useScrollOffset)
            drawList.PopClipRect();

        if (ImGui.IsItemHovered())
        {
            if (drawRaw)
                DrawHistoryHoveredSampleMarker(drawList, rawSamples, sampleCount, plotMin, plotMax, minY, maxY, rawColor);
            if (drawDisplay)
                DrawHistoryHoveredSampleMarker(drawList, displaySamples, sampleCount, plotMin, plotMax, minY, maxY, displayColor);
        }
    }

    private void DrawTimingHistoryHoverTooltip(
        float[] rawSamples,
        float[] displaySamples,
        int sampleCount,
        string units,
        int decimals,
        float latest,
        float average,
        float minimum,
        float maximum,
        bool showRawLine,
        bool showDisplayLine)
    {
        if (!ImGui.IsItemHovered())
            return;

        int sampleIndex = GetHoveredHistorySampleIndex(sampleCount);
        if (sampleIndex < 0)
            return;

        string format = $"F{decimals}";
        ImGui.BeginTooltip();
        ImGui.Text($"Sample {sampleIndex + 1}/{sampleCount}");
        ImGui.Separator();
        if (showRawLine)
            ImGui.Text($"Raw: {rawSamples[sampleIndex].ToString(format)} {units}");
        if (showDisplayLine)
            ImGui.Text($"Display: {displaySamples[sampleIndex].ToString(format)} {units}");
        ImGui.Text($"Latest: {latest.ToString(format)} {units}");
        ImGui.Text($"Average: {average.ToString(format)} {units}");
        ImGui.Text($"Min/Max: {minimum.ToString(format)} / {maximum.ToString(format)} {units}");
        if (string.Equals(units, "ms", StringComparison.Ordinal))
            ImGui.TextDisabled($"Update={FormatUpdateIntervalLabel()}");
        ImGui.EndTooltip();
    }

    private static bool SamplesEqual(float[] left, float[] right, int sampleCount)
    {
        if (left.Length < sampleCount || right.Length < sampleCount)
            return false;

        for (int i = 0; i < sampleCount; i++)
        {
            if (MathF.Abs(left[i] - right[i]) > 0.0001f)
                return false;
        }

        return true;
    }

    private static void ComputeHistoryStats(
        float[] samples,
        int sampleCount,
        float requestedMinY,
        float requestedMaxY,
        out float plotMin,
        out float plotMax,
        out float latest,
        out float average,
        out float minimum,
        out float maximum)
    {
        latest = 0f;
        average = 0f;
        minimum = 0f;
        maximum = 0f;
        plotMin = 0f;
        plotMax = 1f;
        if (sampleCount <= 0)
            return;

        minimum = float.MaxValue;
        maximum = float.MinValue;
        double total = 0.0;

        for (int i = 0; i < sampleCount; i++)
        {
            float value = samples[i];
            if (value < minimum)
                minimum = value;
            if (value > maximum)
                maximum = value;
            total += value;
        }

        latest = samples[sampleCount - 1];
        average = (float)(total / sampleCount);

        if (requestedMaxY > requestedMinY)
        {
            plotMin = requestedMinY;
            plotMax = requestedMaxY;
            return;
        }

        if (!float.IsFinite(minimum) || !float.IsFinite(maximum))
        {
            minimum = 0f;
            maximum = 1f;
            plotMin = 0f;
            plotMax = 1f;
            return;
        }

        float range = maximum - minimum;
        if (range < 0.001f)
            range = MathF.Max(0.1f, MathF.Abs(maximum) * 0.2f);

        float padding = MathF.Max(0.05f, range * 0.12f);
        plotMin = minimum >= 0f ? 0f : minimum - padding;
        plotMax = MathF.Max(plotMin + 0.1f, maximum + padding);
    }

    /// <summary>Returns the latest, average, or worst (max) value from a render-stats ring buffer.</summary>
    private float GetRingBufferStat(float[] ring, ProfilerTimingDisplayMode mode)
    {
        if (_renderStatsHistoryCount <= 0)
            return 0f;

        int count = Math.Min(_renderStatsHistoryCount, RenderStatsHistorySamples);
        int lastIdx = (_renderStatsHistoryHead - 1 + RenderStatsHistorySamples) % RenderStatsHistorySamples;
        float latest = ring[lastIdx];
        if (mode == ProfilerTimingDisplayMode.Latest)
            return latest;

        int start = (_renderStatsHistoryHead - count + RenderStatsHistorySamples) % RenderStatsHistorySamples;
        double sum = 0;
        float max = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            float v = ring[(start + i) % RenderStatsHistorySamples];
            sum += v;
            if (v > max) max = v;
        }

        return mode == ProfilerTimingDisplayMode.Worst ? max : (float)(sum / count);
    }

    /// <summary>Returns the latest, average, or worst (max) value from a GPU pipeline root ring buffer.</summary>
    private float GetGpuPipelineRootStat(string nodeName, float latestMs, ProfilerTimingDisplayMode mode)
    {
        if (mode == ProfilerTimingDisplayMode.Latest || !_gpuPipelineRootHistory.TryGetValue(nodeName, out float[]? series))
            return latestMs;

        return GetRingBufferStat(series, mode);
    }

    private void UpdateFpsDropSpikeLog(ProfilerFramePacket frame, Dictionary<int, float[]> history)
    {
        if (!float.IsFinite(frame.FrameTime)) return;
        if (frame.FrameTime == _lastFpsSpikeProcessedFrameTime) return;
        _lastFpsSpikeProcessedFrameTime = frame.FrameTime;

        if (frame.Threads is null) return;
        foreach (var thread in frame.Threads)
        {
            if (thread is null) continue;
            if (!history.TryGetValue(thread.ThreadId, out var samples) || samples.Length < 2)
                continue;

            float currentMs = samples[^1];
            float previousMs = samples[^2];
            if (currentMs <= 0.0001f || previousMs <= 0.0001f) continue;

            float currentFps = 1000f / currentMs;
            float previousFps = 1000f / previousMs;
            if (previousFps < FpsSpikeMinPreviousFps) continue;
            if (currentMs - previousMs < FpsSpikeMinDeltaMs) continue;

            float baselineMs = GetMedianTailMs(samples, FpsSpikeBaselineWindowSamples, skipFromEnd: 1);
            if (baselineMs <= 0.0001f) continue;

            float baselineFps = 1000f / baselineMs;
            float comparisonFps = MathF.Min(previousFps, baselineFps);
            if (comparisonFps <= 0.0001f) continue;

            float deltaFps = comparisonFps - currentFps;
            if (deltaFps < _fpsSpikeMinDropFps) continue;

            float dropFraction = Math.Clamp(deltaFps / comparisonFps, 0f, 1f);

            string hotPath = GetHottestPath(thread.RootNodes, out _);
            string key = $"{thread.ThreadId}:{hotPath}";
            var nowUtc = DateTime.UtcNow;
            var candidate = new FpsDropSpikePathEntry(
                thread.ThreadId, hotPath, 1, nowUtc, nowUtc,
                frame.FrameTime, comparisonFps, currentFps, deltaFps, dropFraction);

            if (_fpsDropSpikePathIndexByKey.TryGetValue(key, out int idx))
            {
                var existing = _fpsDropSpikePaths[idx];
                var updated = existing with { SeenCount = existing.SeenCount + 1, LastSeenUtc = nowUtc };
                if (deltaFps > existing.WorstDeltaFps)
                    updated = updated with
                    {
                        WorstFrameTimeSeconds = frame.FrameTime,
                        WorstComparisonFps = comparisonFps,
                        WorstCurrentFps = currentFps,
                        WorstDeltaFps = deltaFps,
                        WorstDropFraction = dropFraction
                    };
                _fpsDropSpikePaths[idx] = updated;
            }
            else
            {
                _fpsDropSpikePathIndexByKey[key] = _fpsDropSpikePaths.Count;
                _fpsDropSpikePaths.Add(candidate);
            }
        }
    }

    private void ResolveSpikeSort()
    {
        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsCount == 0) return;

        var primary = sortSpecs.Specs;
        int col = primary.ColumnIndex;
        var dir = primary.SortDirection;
        bool countChanged = _fpsDropSpikeLastSortedCount != _fpsDropSpikePaths.Count;
        bool sortChanged = col != _fpsDropSpikeLastSortColumn || dir != _fpsDropSpikeLastSortDirection;

        if (sortSpecs.SpecsDirty || countChanged || sortChanged)
        {
            _fpsDropSpikeSortedIndices.Clear();
            _fpsDropSpikeSortedIndices.Capacity = Math.Max(_fpsDropSpikeSortedIndices.Capacity, _fpsDropSpikePaths.Count);
            for (int i = 0; i < _fpsDropSpikePaths.Count; i++)
                _fpsDropSpikeSortedIndices.Add(i);

            if (dir != ImGuiSortDirection.None)
                _fpsDropSpikeSortedIndices.Sort((a, b) => CompareSpikeRows(_fpsDropSpikePaths[a], _fpsDropSpikePaths[b], col, dir));

            _fpsDropSpikeLastSortedCount = _fpsDropSpikePaths.Count;
            _fpsDropSpikeLastSortColumn = col;
            _fpsDropSpikeLastSortDirection = dir;
            sortSpecs.SpecsDirty = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Hierarchy drawing helpers
    // ═══════════════════════════════════════════════════════════════

    private void DrawAggregatedRootMethodHierarchy(ProfilerRootMethodAggregate rm, DateTime nowUtc)
    {
        var children = rm.CachedSortedChildren;
        float untracked = rm.CachedUntrackedTime;
        int allCount = rm.Children.Count;
        bool isStale = !_paused && nowUtc - rm.LastSeen > TimeSpan.FromSeconds(GetStaleWindowSeconds());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
        if (children.Length == 0 && (untracked < 0.1f || allCount == 0))
            flags |= ImGuiTreeNodeFlags.Leaf;

        string key = $"Root_{rm.Name}";
        if (_nodeOpenCache.TryGetValue(key, out bool wasOpen) && wasOpen)
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        if (isStale) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));
        string label = isStale ? $"{rm.Name} (aggregated) (inactive)##{key}" : $"{rm.Name} (aggregated)##{key}";
        bool nodeOpen = ImGui.TreeNodeEx(label, flags);
        if (isStale) ImGui.PopStyleColor();
        if (ImGui.IsItemToggledOpen())
            _nodeOpenCache[key] = nodeOpen;

        if (isStale) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));
        int hierCalls = Math.Max(1, rm.DisplayRootNodeCount);
        float hierAvgMs = rm.DisplayTotalTimeMs / hierCalls;
        ImGui.TableSetColumnIndex(1); ImGui.Text(hierCalls > 1 ? $"{hierAvgMs:F3} ({rm.DisplayTotalTimeMs:F3})" : $"{rm.DisplayTotalTimeMs:F3}");
        ImGui.TableSetColumnIndex(2); ImGui.Text($"{rm.DisplayRootNodeCount}");
        if (isStale) ImGui.PopStyleColor();

        if (nodeOpen)
        {
            for (int i = 0; i < children.Length; i++)
                DrawAggregatedChildNode(children[i], key, nowUtc);

            if (untracked >= 0.1f && allCount > 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TreeNodeEx($"(untracked time)##Untracked_{key}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);
                ImGui.TableSetColumnIndex(1); ImGui.Text($"{untracked:F3}");
                ImGui.TableSetColumnIndex(2); ImGui.Text("-");
            }

            ImGui.TreePop();
        }
    }

    private void DrawAggregatedChildNode(AggregatedChildNode node, string idSuffix, DateTime nowUtc)
    {
        var children = node.CachedSortedChildren;
        float untracked = node.CachedUntrackedTime;
        int allCount = node.Children.Count;
        bool isStale = !_paused && nowUtc - node.LastSeen > TimeSpan.FromSeconds(GetStaleWindowSeconds());

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
        if (children.Length == 0 && (untracked < 0.1f || allCount == 0))
            flags |= ImGuiTreeNodeFlags.Leaf;

        string key = $"{node.Name}_{idSuffix}";
        if (_nodeOpenCache.TryGetValue(key, out bool wasOpen) && wasOpen)
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (isStale) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));
        string label = isStale ? $"{node.Name} (inactive)##{key}" : $"{node.Name}##{key}";
        bool nodeOpen = ImGui.TreeNodeEx(label, flags);
        if (isStale) ImGui.PopStyleColor();
        if (ImGui.IsItemToggledOpen())
            _nodeOpenCache[key] = nodeOpen;

        if (isStale) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));
        ImGui.TableSetColumnIndex(1); ImGui.Text($"{node.DisplayTotalElapsedMs:F3}");
        ImGui.TableSetColumnIndex(2); ImGui.Text($"{node.DisplayCallCount}");
        if (isStale) ImGui.PopStyleColor();

        if (nodeOpen)
        {
            for (int i = 0; i < children.Length; i++)
                DrawAggregatedChildNode(children[i], key, nowUtc);

            if (untracked >= 0.1f && allCount > 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TreeNodeEx($"(untracked time)##Untracked_{key}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);
                ImGui.TableSetColumnIndex(1); ImGui.Text($"{untracked:F3}");
                ImGui.TableSetColumnIndex(2); ImGui.Text("-");
            }

            ImGui.TreePop();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Recursive aggregation helpers
    // ═══════════════════════════════════════════════════════════════

    private static void UpdateAggregatedChildrenRecursive(ProfilerNodeData[]? sourceChildren, Dictionary<string, AggregatedChildNode> target, DateTime now)
    {
        if (sourceChildren is null) return;
        foreach (var child in sourceChildren)
        {
            if (child is null) continue;
            if (!target.TryGetValue(child.Name, out var agg))
            {
                agg = new AggregatedChildNode { Name = child.Name };
                target[child.Name] = agg;
            }
            agg.TotalElapsedMs += child.ElapsedMs;
            agg.CallCount++;
            agg.SeenThisUpdate = true;
            agg.LastSeen = now;
            UpdateAggregatedChildrenRecursive(child.Children, agg.Children, now);
        }
    }

    private static void ResetAggregatedChildrenStats(Dictionary<string, AggregatedChildNode> children)
    {
        foreach (var c in children.Values)
        {
            c.TotalElapsedMs = 0;
            c.CallCount = 0;
            ResetAggregatedChildrenStats(c.Children);
        }
    }

    private static void UpdateAggregatedChildrenMaxRecursive(Dictionary<string, AggregatedChildNode> children)
    {
        foreach (var c in children.Values)
        {
            c.AccumulatedMaxTotalElapsedMs = Math.Max(c.AccumulatedMaxTotalElapsedMs, c.TotalElapsedMs);
            c.AccumulatedMaxCallCount = Math.Max(c.AccumulatedMaxCallCount, c.CallCount);
            UpdateAggregatedChildrenMaxRecursive(c.Children);
        }
    }

    private void UpdateAggregatedChildrenDisplayRecursive(Dictionary<string, AggregatedChildNode> children)
    {
        foreach (var c in children.Values)
        {
            if (c.SeenThisUpdate)
            {
                c.DisplayTotalElapsedMs = ApplyDisplaySmoothing(c.DisplayTotalElapsedMs, c.AccumulatedMaxTotalElapsedMs);
                c.DisplayCallCount = c.AccumulatedMaxCallCount;
            }
            c.AccumulatedMaxTotalElapsedMs = 0;
            c.AccumulatedMaxCallCount = 0;
            c.SeenThisUpdate = false;
            UpdateAggregatedChildrenDisplayRecursive(c.Children);
        }
    }

    private void PruneAggregatedChildren(Dictionary<string, AggregatedChildNode> children, DateTime now)
    {
        if (!_paused)
        {
            _childKeysToRemove.Clear();
            var threshold = TimeSpan.FromSeconds(_persistenceSeconds);
            foreach (var kvp in children)
            {
                if (now - kvp.Value.LastSeen > threshold)
                    _childKeysToRemove.Add(kvp.Key);
            }
            foreach (var key in _childKeysToRemove)
                children.Remove(key);
        }
        foreach (var c in children.Values)
            PruneAggregatedChildren(c.Children, now);
    }

    private void RebuildCachedChildrenRecursive(Dictionary<string, AggregatedChildNode> children, float parentTime, out AggregatedChildNode[] sorted, out float untracked, DateTime nowUtc, float staleSec)
    {
        float childTotal = 0f;
        int visible = 0;
        foreach (var c in children.Values)
        {
            childTotal += c.DisplayTotalElapsedMs;
            if (c.DisplayTotalElapsedMs >= 0.1f || _paused || (nowUtc - c.LastSeen).TotalSeconds < staleSec)
                visible++;
        }
        untracked = parentTime - childTotal;

        sorted = visible > 0 ? new AggregatedChildNode[visible] : [];
        int idx = 0;
        foreach (var c in children.Values)
        {
            if (c.DisplayTotalElapsedMs >= 0.1f || _paused || (nowUtc - c.LastSeen).TotalSeconds < staleSec)
            {
                sorted[idx++] = c;
                RebuildCachedChildrenRecursive(c.Children, c.DisplayTotalElapsedMs, out var childSorted, out var childUntracked, nowUtc, staleSec);
                c.CachedSortedChildren = childSorted;
                c.CachedUntrackedTime = childUntracked;
            }
        }

        if (_sortByTime && sorted.Length > 1)
            Array.Sort(sorted, static (a, b) => b.DisplayTotalElapsedMs.CompareTo(a.DisplayTotalElapsedMs));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Static helpers
    // ═══════════════════════════════════════════════════════════════

    private static void DrawAllocRow(string name, AllocationSlice? slice)
    {
        if (slice is null) return;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0); ImGui.Text(name);
        ImGui.TableSetColumnIndex(1); ImGui.Text($"{slice.LastKB:F2}");
        ImGui.TableSetColumnIndex(2); ImGui.Text($"{slice.AverageKB:F2}");
        ImGui.TableSetColumnIndex(3); ImGui.Text($"{slice.MaxKB:F2}");
        ImGui.TableSetColumnIndex(4); ImGui.Text($"{slice.Samples}/{slice.Capacity}");
    }

    private static float GetMedianTailMs(float[] samples, int takeCount, int skipFromEnd)
    {
        int available = samples.Length - skipFromEnd;
        if (available <= 0) return 0f;
        int count = Math.Min(takeCount, available);
        if (count <= 0) return 0f;

        float[] window = new float[count];
        Array.Copy(samples, available - count, window, 0, count);
        Array.Sort(window);
        int mid = count / 2;
        return (count % 2 == 0) ? (window[mid - 1] + window[mid]) * 0.5f : window[mid];
    }

    private static string GetHottestPath(ProfilerNodeData[]? roots, out float pathMs)
    {
        pathMs = 0f;
        if (roots is null || roots.Length == 0) return "(no samples)";

        var hottest = roots[0];
        for (int i = 1; i < roots.Length; i++)
        {
            if (roots[i] is not null && roots[i].ElapsedMs > hottest.ElapsedMs)
                hottest = roots[i];
        }
        if (hottest is null) return "(no samples)";

        pathMs = hottest.ElapsedMs;
        var parts = new List<string>(8) { hottest.Name };
        var current = hottest;

        while (current.Children is { Length: > 0 })
        {
            var best = current.Children[0];
            for (int i = 1; i < current.Children.Length; i++)
            {
                if (current.Children[i] is not null && current.Children[i].ElapsedMs > best.ElapsedMs)
                    best = current.Children[i];
            }
            if (best is null) break;
            parts.Add(best.Name);
            current = best;
        }

        return string.Join(" > ", parts);
    }

    private static int CompareSpikeRows(FpsDropSpikePathEntry a, FpsDropSpikePathEntry b, int col, ImGuiSortDirection dir)
    {
        int sign = dir == ImGuiSortDirection.Descending ? -1 : 1;
        int r = col switch
        {
            0 => a.WorstFrameTimeSeconds.CompareTo(b.WorstFrameTimeSeconds),
            1 => a.ThreadId.CompareTo(b.ThreadId),
            2 => a.SeenCount.CompareTo(b.SeenCount),
            3 => a.WorstComparisonFps.CompareTo(b.WorstComparisonFps),
            4 => a.WorstCurrentFps.CompareTo(b.WorstCurrentFps),
            5 => a.WorstDeltaFps.CompareTo(b.WorstDeltaFps),
            6 => a.WorstDropFraction.CompareTo(b.WorstDropFraction),
            7 => string.CompareOrdinal(a.HotPath, b.HotPath),
            _ => 0,
        };
        if (r == 0)
            r = a.ThreadId != b.ThreadId ? a.ThreadId.CompareTo(b.ThreadId) : string.CompareOrdinal(a.HotPath, b.HotPath);
        return r * sign;
    }

    /// <summary>Formats a byte count as B, KB, or MB.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static string FormatTickGroupMask(int tickGroupMask)
    {
        if (tickGroupMask == 0)
            return "-";

        string result = string.Empty;
        if ((tickGroupMask & (1 << 0)) != 0)
            result = "Normal";
        if ((tickGroupMask & (1 << 1)) != 0)
            result = result.Length == 0 ? "Late" : $"{result}, Late";
        if ((tickGroupMask & (1 << 2)) != 0)
            result = result.Length == 0 ? "PrePhysics" : $"{result}, PrePhysics";
        if ((tickGroupMask & (1 << 3)) != 0)
            result = result.Length == 0 ? "DuringPhysics" : $"{result}, DuringPhysics";
        if ((tickGroupMask & (1 << 4)) != 0)
            result = result.Length == 0 ? "PostPhysics" : $"{result}, PostPhysics";
        return result.Length > 0 ? result : "-";
    }

    // ═══════════════════════════════════════════════════════════════
    //  Inner data types
    // ═══════════════════════════════════════════════════════════════

    private sealed class AggregatedChildNode
    {
        public string Name { get; set; } = string.Empty;
        public float TotalElapsedMs { get; set; }
        public int CallCount { get; set; }
        public bool SeenThisUpdate { get; set; }
        public float AccumulatedMaxTotalElapsedMs { get; set; }
        public int AccumulatedMaxCallCount { get; set; }
        public float DisplayTotalElapsedMs { get; set; }
        public int DisplayCallCount { get; set; }
        public float CachedUntrackedTime { get; set; }
        public Dictionary<string, AggregatedChildNode> Children { get; } = new();
        public AggregatedChildNode[] CachedSortedChildren { get; set; } = [];
        public DateTime LastSeen { get; set; }
    }

    private sealed class ProfilerRootMethodAggregate
    {
        public string Name { get; set; } = string.Empty;
        public float TotalTimeMs { get; set; }
        public bool SeenThisUpdate { get; set; }
        public float AccumulatedMaxTotalTimeMs { get; set; }
        public int AccumulatedMaxThreadCount { get; set; }
        public int AccumulatedMaxRootNodeCount { get; set; }
        public float DisplayTotalTimeMs { get; set; }
        public int DisplayThreadCount { get; set; }
        public int DisplayRootNodeCount { get; set; }
        public float CachedUntrackedTime { get; set; }
        public HashSet<int> ThreadIds { get; } = new();
        public List<ProfilerNodeData> RootNodes { get; } = new();
        public float[] Samples { get; set; } = Array.Empty<float>();
        public float[] RawHzSamples { get; set; } = Array.Empty<float>();
        public float[] DisplayHzSamples { get; set; } = Array.Empty<float>();
        public DateTime LastSeen { get; set; }
        public Dictionary<string, AggregatedChildNode> Children { get; } = new();
        public AggregatedChildNode[] CachedSortedChildren { get; set; } = [];

        // Per-method per-snapshot history (maintained by the renderer)
        public Queue<float> PerCallMsHistory { get; } = new();
        public Queue<int> CallCountHistory { get; } = new();
        public const int MethodHistoryCapacity = 720;
    }

    private sealed class ProfilerThreadCacheEntry
    {
        public int ThreadId;
        public DateTime LastSeen;
        public bool IsStale;
    }

    private readonly record struct RootMethodActivitySortKey(int Category, float ActiveRatio, float AverageCallCount);

    private sealed class TimingGraphInterpolationState
    {
        public TimingGraphInterpolationState(int sampleCount)
        {
            SampleCount = sampleCount;
            StartSamples = new float[sampleCount];
            TargetSamples = new float[sampleCount];
        }

        public int SampleCount { get; }
        public float[] StartSamples { get; }
        public float[] TargetSamples { get; }
        public bool HasPrevious { get; set; }
        public float LastInterpolationT { get; set; } = 1.0f;
        public DateTime LastRefreshUtc { get; set; }
    }

    private readonly record struct FpsDropSpikePathEntry(
        int ThreadId,
        string HotPath,
        int SeenCount,
        DateTime FirstSeenUtc,
        DateTime LastSeenUtc,
        float WorstFrameTimeSeconds,
        float WorstComparisonFps,
        float WorstCurrentFps,
        float WorstDeltaFps,
        float WorstDropFraction);
}
