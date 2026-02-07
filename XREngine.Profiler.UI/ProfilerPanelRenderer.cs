using System.Numerics;
using ImGuiNET;
using XREngine.Data.Profiling;

namespace XREngine.Profiler.UI;

/// <summary>
/// Shared profiler panel renderer — all aggregation, caching, and ImGui drawing
/// logic. Works with any <see cref="IProfilerDataSource"/> implementation
/// (in-process engine data or remote UDP packets).
/// </summary>
public sealed class ProfilerPanelRenderer
{
    private readonly IProfilerDataSource _source;

    public ProfilerPanelRenderer(IProfilerDataSource source)
    {
        _source = source;
    }

    // ──────────────── Root method aggregation cache ────────────────

    private readonly Dictionary<string, ProfilerRootMethodAggregate> _rootMethodCache = new();
    private float _persistenceSeconds = 5.0f;
    private float _updateIntervalSeconds = 0.5f;
    private float _lastEnqueuedFrameTime = float.NegativeInfinity;

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
    private float _rootHierarchyMinMs;
    private float _rootHierarchyMaxMs;
    private readonly Dictionary<string, bool> _nodeOpenCache = new();

    // ═══════════════════════════════════════════════════════════════
    //  Public entry points — called per-frame
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Process the latest data from the source (call once per frame).</summary>
    public void ProcessLatestData()
    {
        if (_paused) return;

        var frame = _source.LatestFrame;
        if (frame is null || frame.Threads is null || frame.Threads.Length == 0)
            return;

        bool isNew = frame.FrameTime != _lastEnqueuedFrameTime;
        if (!isNew) return;

        _lastEnqueuedFrameTime = frame.FrameTime;
        var history = frame.ThreadHistory ?? new Dictionary<int, float[]>();
        _lastHistorySnapshot = history;

        var nowUtc = DateTime.UtcNow;
        UpdateWorstFrameStatistics(frame, nowUtc);
        var display = GetSnapshotForHierarchy(frame, out float hierMs, out bool usingWorst);
        UpdateThreadCache(frame.Threads, nowUtc);
        UpdateRootMethodCache(frame, history, nowUtc);
        UpdateFpsDropSpikeLog(frame, history);
        UpdateDisplayValues();
        RebuildCachedRootMethodLists(nowUtc);

        _lastCaptureTime = frame.FrameTime;
        _lastHierarchySnapshot = display;
        _lastHierarchyFrameMs = hierMs;
        _lastHierarchyUsingWorst = usingWorst;
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
            if (!ImGui.Begin("Profiler Tree", ref open))
            {
                ImGui.End();
                return;
            }
        }
        else
        {
            if (!ImGui.Begin("Profiler Tree"))
            {
                ImGui.End();
                return;
            }
        }

        var nowUtc = DateTime.UtcNow;

        // Controls
        ImGui.Checkbox("Pause", ref _paused);
        ImGui.SameLine();
        ImGui.Checkbox("Sort by Time", ref _sortByTime);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("Update (s)", ref _updateIntervalSeconds, 0.05f, 0.1f, 2.0f);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("Persist (s)", ref _persistenceSeconds, 0.1f, 0.5f, 10.0f);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.DragFloat("Min ms", ref _rootHierarchyMinMs, 0.05f, 0.0f, 1000.0f);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.DragFloat("Max ms", ref _rootHierarchyMaxMs, 0.05f, 0.0f, 1000.0f);

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
            foreach (var rm in _cachedGraphList)
            {
                bool isStale = nowUtc - rm.LastSeen > TimeSpan.FromSeconds(_updateIntervalSeconds);
                float fps = rm.DisplayTotalTimeMs > 0.001f ? 1000.0f / rm.DisplayTotalTimeMs : 0.0f;
                string label = $"{rm.Name} ({rm.DisplayTotalTimeMs:F3} ms, {fps:F1} FPS, {rm.ThreadIds.Count} thread(s))";
                if (isStale) label += " (inactive)";

                if (isStale) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));
                ImGui.Text(label);
                if (isStale) ImGui.PopStyleColor();

                if (rm.Samples.Length > 0)
                {
                    float min = float.MaxValue, max = float.MinValue;
                    for (int i = 0; i < rm.Samples.Length; i++)
                    {
                        float v = rm.Samples[i];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    if (!float.IsFinite(min) || !float.IsFinite(max)) { min = 0f; max = 0f; }
                    if (MathF.Abs(max - min) < 0.001f) max = min + 0.001f;

                    ImGui.PlotLines($"##Plot_{rm.Name}", ref rm.Samples[0], rm.Samples.Length, 0, null, min, max, new Vector2(-1f, 40f));
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
                    if (rm.RootNodes.Count > 0)
                        DrawAggregatedRootMethodHierarchy(rm);
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
        foreach (var entry in _threadCache.Values)
        {
            if (now - entry.LastSeen > ThreadStaleThreshold)
                entry.IsStale = true;
        }

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
                    agg.LastSeen = now;
                    UpdateAggregatedChildrenRecursive(rootNode.Children, agg.Children, now);
                }
            }
        }

        foreach (var entry in _rootMethodCache.Values)
        {
            entry.AccumulatedMaxTotalTimeMs = Math.Max(entry.AccumulatedMaxTotalTimeMs, entry.TotalTimeMs);
            UpdateAggregatedChildrenMaxRecursive(entry.Children);
        }

        foreach (var entry in _rootMethodCache.Values)
            PruneAggregatedChildren(entry.Children, now);

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

        if (!_paused)
        {
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
    }

    private void UpdateDisplayValues()
    {
        foreach (var entry in _rootMethodCache.Values)
        {
            entry.DisplayTotalTimeMs = entry.AccumulatedMaxTotalTimeMs;
            entry.AccumulatedMaxTotalTimeMs = 0;
            UpdateAggregatedChildrenDisplayRecursive(entry.Children);
        }
    }

    private void RebuildCachedRootMethodLists(DateTime nowUtc)
    {
        float minMs = Math.Max(0f, _rootHierarchyMinMs);
        float maxMs = _rootHierarchyMaxMs;
        bool hasMax = maxMs > 0f;
        var staleSec = _persistenceSeconds;

        _cachedGraphList.Clear();
        foreach (var rm in _rootMethodCache.Values)
        {
            bool visible = rm.DisplayTotalTimeMs >= 0.1f || _paused || (nowUtc - rm.LastSeen).TotalSeconds < staleSec;
            bool inRange = rm.DisplayTotalTimeMs >= minMs && (!hasMax || rm.DisplayTotalTimeMs <= maxMs);
            if (visible && inRange)
                _cachedGraphList.Add(rm);
        }
        _cachedGraphList.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

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
            _cachedHierarchyList.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
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

    private void DrawAggregatedRootMethodHierarchy(ProfilerRootMethodAggregate rm)
    {
        var children = rm.CachedSortedChildren;
        float untracked = rm.CachedUntrackedTime;
        int allCount = rm.Children.Count;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
        if (children.Length == 0 && (untracked < 0.1f || allCount == 0))
            flags |= ImGuiTreeNodeFlags.Leaf;

        string key = $"Root_{rm.Name}";
        if (_nodeOpenCache.TryGetValue(key, out bool wasOpen) && wasOpen)
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        bool nodeOpen = ImGui.TreeNodeEx($"{rm.Name} (aggregated)##{key}", flags);
        if (ImGui.IsItemToggledOpen())
            _nodeOpenCache[key] = nodeOpen;

        ImGui.TableSetColumnIndex(1); ImGui.Text($"{rm.DisplayTotalTimeMs:F3}");
        ImGui.TableSetColumnIndex(2); ImGui.Text($"{rm.RootNodes.Count}");

        if (nodeOpen)
        {
            for (int i = 0; i < children.Length; i++)
                DrawAggregatedChildNode(children[i], key);

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

    private void DrawAggregatedChildNode(AggregatedChildNode node, string idSuffix)
    {
        var children = node.CachedSortedChildren;
        float untracked = node.CachedUntrackedTime;
        int allCount = node.Children.Count;

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
        if (children.Length == 0 && (untracked < 0.1f || allCount == 0))
            flags |= ImGuiTreeNodeFlags.Leaf;

        string key = $"{node.Name}_{idSuffix}";
        if (_nodeOpenCache.TryGetValue(key, out bool wasOpen) && wasOpen)
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        bool nodeOpen = ImGui.TreeNodeEx($"{node.Name}##{key}", flags);
        if (ImGui.IsItemToggledOpen())
            _nodeOpenCache[key] = nodeOpen;

        ImGui.TableSetColumnIndex(1); ImGui.Text($"{node.DisplayTotalElapsedMs:F3}");
        ImGui.TableSetColumnIndex(2); ImGui.Text($"{node.DisplayCallCount}");

        if (nodeOpen)
        {
            for (int i = 0; i < children.Length; i++)
                DrawAggregatedChildNode(children[i], key);

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

    private static void UpdateAggregatedChildrenDisplayRecursive(Dictionary<string, AggregatedChildNode> children)
    {
        foreach (var c in children.Values)
        {
            c.DisplayTotalElapsedMs = c.AccumulatedMaxTotalElapsedMs;
            c.DisplayCallCount = c.AccumulatedMaxCallCount;
            c.AccumulatedMaxTotalElapsedMs = 0;
            c.AccumulatedMaxCallCount = 0;
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

    // ═══════════════════════════════════════════════════════════════
    //  Inner data types
    // ═══════════════════════════════════════════════════════════════

    private sealed class AggregatedChildNode
    {
        public string Name { get; set; } = string.Empty;
        public float TotalElapsedMs { get; set; }
        public int CallCount { get; set; }
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
        public float AccumulatedMaxTotalTimeMs { get; set; }
        public float DisplayTotalTimeMs { get; set; }
        public float CachedUntrackedTime { get; set; }
        public HashSet<int> ThreadIds { get; } = new();
        public List<ProfilerNodeData> RootNodes { get; } = new();
        public float[] Samples { get; set; } = Array.Empty<float>();
        public DateTime LastSeen { get; set; }
        public Dictionary<string, AggregatedChildNode> Children { get; } = new();
        public AggregatedChildNode[] CachedSortedChildren { get; set; } = [];
    }

    private sealed class ProfilerThreadCacheEntry
    {
        public int ThreadId;
        public DateTime LastSeen;
        public bool IsStale;
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
