using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        // Cache for root method aggregation across threads
        private static readonly Dictionary<string, ProfilerRootMethodAggregate> _profilerRootMethodCache = new();
        private static float _profilerPersistenceSeconds = 5.0f;
        private static DateTime _lastProfilerUIUpdate = DateTime.MinValue;
        private static float _profilerUpdateIntervalSeconds = 0.5f;
        private static float _lastEnqueuedSnapshotTime = float.NegativeInfinity;

        private const int FpsSpikeBaselineWindowSamples = 30;
        private const float FpsSpikeMinPreviousFps = 10.0f;
        private const float FpsSpikeMinDeltaMs = 1.0f;
        private static float _fpsSpikeMinDropFps = 15.0f;
        private static float _lastFpsSpikeProcessedFrameTime = float.NegativeInfinity;
        private static readonly List<FpsDropSpikePathEntry> _fpsDropSpikePaths = new();
        private static readonly Dictionary<string, int> _fpsDropSpikePathIndexByKey = new(StringComparer.Ordinal);

        private static readonly List<int> _fpsDropSpikeSortedIndices = new();
        private static int _fpsDropSpikeLastSortedCount;
        private static int _fpsDropSpikeLastSortColumn = -1;
        private static ImGuiSortDirection _fpsDropSpikeLastSortDirection = ImGuiSortDirection.None;

        private static bool _profilerPaused;
        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _lastProfilerHierarchySnapshot;
        private static float _lastProfilerHierarchyFrameMs;
        private static bool _lastProfilerHierarchyUsingWorstWindowSample;
        private static float _profilerRootHierarchyMinMs;
        private static float _profilerRootHierarchyMaxMs;
        private static Dictionary<int, float[]> _lastProfilerHistorySnapshot = new();

        // Cached sorted lists to avoid per-frame LINQ allocations
        private static readonly List<ProfilerRootMethodAggregate> _cachedRootMethodGraphList = new();
        private static readonly List<ProfilerRootMethodAggregate> _cachedRootMethodHierarchyList = new();

        private static void DrawProfilerPanel()
        {
            if (!_showProfiler) return;
            if (!ImGui.Begin("Profiler", ref _showProfiler))
            {
                ImGui.End();
                return;
            }
            DrawProfilerTabContent();
            ImGui.End();
        }

        private static void DrawProfilerTabContent()
        {
            var nowUtc = DateTime.UtcNow;

            // Profiler controls
            bool paused = _profilerPaused;
            if (ImGui.Checkbox("Pause Profiler", ref paused))
                _profilerPaused = paused;

            ImGui.SameLine();
            bool enableFrameLogging = Engine.EditorPreferences.Debug.EnableProfilerFrameLogging;
            if (ImGui.Checkbox("Enable Frame Logging", ref enableFrameLogging))
                Engine.EditorPreferences.Debug.EnableProfilerFrameLogging = enableFrameLogging;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, profiler method timing is skipped to reduce overhead.");

            ImGui.SameLine();
            bool enableStatsTracking = Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking;
            if (ImGui.Checkbox("Enable Stats Tracking", ref enableStatsTracking))
                Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking = enableStatsTracking;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, per-frame render statistics (draw calls, triangles) are not tracked.");

            Engine.CodeProfiler.ProfilerFrameSnapshot? snapshotForDisplay = null;
            float hierarchyFrameMs = 0.0f;
            bool showingWorstWindowSample = false;

            if (_profilerPaused)
            {
                snapshotForDisplay = _lastProfilerHierarchySnapshot;
                hierarchyFrameMs = _lastProfilerHierarchyFrameMs;
                showingWorstWindowSample = _lastProfilerHierarchyUsingWorstWindowSample;
            }
            else
            {
                // Lock-free read of profiler snapshot - no blocking, no requesting
                Engine.Profiler.TryGetSnapshot(out var frameSnapshot, out var history);
                
                if (frameSnapshot is not null && frameSnapshot.Threads.Count > 0)
                {
                    // Only process if this is a new snapshot
                    bool isNewSnapshot = frameSnapshot.FrameTime != _lastEnqueuedSnapshotTime;
                    if (isNewSnapshot)
                    {
                        _lastEnqueuedSnapshotTime = frameSnapshot.FrameTime;
                        _lastProfilerHistorySnapshot = history;
                        ProcessProfilerSnapshotInline(frameSnapshot, history, nowUtc);
                    }
                }
                
                // Use cached values for display
                snapshotForDisplay = _lastProfilerHierarchySnapshot;
                hierarchyFrameMs = _lastProfilerHierarchyFrameMs;
                showingWorstWindowSample = _lastProfilerHierarchyUsingWorstWindowSample;
            }

            if (_profilerThreadCache.Count == 0)
            {
                ImGui.Text("No profiler samples captured yet.");
                return;
            }

            if (snapshotForDisplay is not null)
            {
                ImGui.Text($"Captured at {_lastProfilerCaptureTime:F3}s");
                ImGui.Text($"Worst frame (0.5s window): {hierarchyFrameMs:F3} ms");
                if (showingWorstWindowSample)
                    ImGui.Text("Hierarchy shows worst frame snapshot from the rolling window.");
            }
            else
            {
                ImGui.Text($"Awaiting fresh profiler samples… (last capture at {_lastProfilerCaptureTime:F3}s)");
                ImGui.Text($"Worst frame (0.5s window): {_worstFrameDisplayMs:F3} ms");
            }

            ImGui.Separator();
            if (ImGui.CollapsingHeader("Thread Allocations", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool enabled = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking;
                if (ImGui.Checkbox("Enable thread allocation tracking", ref enabled))
                    Engine.EditorPreferences.Debug.EnableThreadAllocationTracking = enabled;

                ImGui.TextDisabled("Uses GC.GetAllocatedBytesForCurrentThread() deltas per tick/frame.");

                var alloc = Engine.Allocations.GetSnapshot();

                if (ImGui.BeginTable("ProfilerThreadAllocations", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Thread");
                    ImGui.TableSetupColumn("Last (KB)");
                    ImGui.TableSetupColumn("Avg (KB)");
                    ImGui.TableSetupColumn("Max (KB)");
                    ImGui.TableSetupColumn("Samples");
                    ImGui.TableHeadersRow();

                    DrawAllocRow("Render", alloc.Render);
                    DrawAllocRow("Collect+Swap", alloc.CollectSwap);
                    DrawAllocRow("Update", alloc.Update);
                    DrawAllocRow("FixedUpdate", alloc.FixedUpdate);

                    ImGui.EndTable();
                }
            }

            // Display rendering statistics
            ImGui.Separator();
            if (ImGui.CollapsingHeader("Rendering Statistics", ImGuiTreeNodeFlags.DefaultOpen))
            {
                int drawCalls = Engine.Rendering.Stats.DrawCalls;
                int multiDrawCalls = Engine.Rendering.Stats.MultiDrawCalls;
                int triangles = Engine.Rendering.Stats.TrianglesRendered;

                ImGui.Text($"Draw Calls: {drawCalls:N0}");
                ImGui.Text($"Multi-Draw Calls: {multiDrawCalls:N0}");
                ImGui.Text($"Triangles Rendered: {triangles:N0}");

                ImGui.Separator();
                ImGui.Text("GPU VRAM Usage:");
                
                double totalVRAM = Engine.Rendering.Stats.AllocatedVRAMMB;
                long bufferBytes = Engine.Rendering.Stats.AllocatedBufferBytes;
                long textureBytes = Engine.Rendering.Stats.AllocatedTextureBytes;
                long renderBufferBytes = Engine.Rendering.Stats.AllocatedRenderBufferBytes;

                ImGui.Text($"  Total: {totalVRAM:F2} MB");
                ImGui.Text($"  Buffers: {bufferBytes / (1024.0 * 1024.0):F2} MB");
                ImGui.Text($"  Textures: {textureBytes / (1024.0 * 1024.0):F2} MB");
                ImGui.Text($"  Render Buffers: {renderBufferBytes / (1024.0 * 1024.0):F2} MB");

                ImGui.Separator();
                ImGui.Text("FBO Render Bandwidth:");
                
                double fboBandwidthMB = Engine.Rendering.Stats.FBOBandwidthMB;
                int fboBindCount = Engine.Rendering.Stats.FBOBindCount;

                ImGui.Text($"  Bandwidth: {fboBandwidthMB:F2} MB/frame");
                ImGui.Text($"  FBO Binds: {fboBindCount:N0}");

                ImGui.Separator();
                ImGui.Text("Render Matrix Updates:");

                bool enableRenderMatrixStats = Engine.Rendering.Stats.EnableRenderMatrixStats;
                if (ImGui.Checkbox("Enable render-matrix stats", ref enableRenderMatrixStats))
                    Engine.Rendering.Stats.EnableRenderMatrixStats = enableRenderMatrixStats;

                bool enableRenderMatrixListeners = Engine.Rendering.Stats.EnableRenderMatrixListenerTracking;
                if (ImGui.Checkbox("Enable render-matrix listener tracking", ref enableRenderMatrixListeners))
                    Engine.Rendering.Stats.EnableRenderMatrixListenerTracking = enableRenderMatrixListeners;

                if (!Engine.Rendering.Stats.RenderMatrixStatsReady)
                {
                    ImGui.TextDisabled("  Waiting for stats...");
                }
                else
                {
                    ImGui.Text($"  Applied (swap): {Engine.Rendering.Stats.RenderMatrixApplied:N0}");
                    ImGui.Text($"  SetRenderMatrix calls: {Engine.Rendering.Stats.RenderMatrixSetCalls:N0}");
                    if (Engine.Rendering.Stats.EnableRenderMatrixListenerTracking)
                        ImGui.Text($"  Listener invocations: {Engine.Rendering.Stats.RenderMatrixListenerInvocations:N0}");
                    else
                        ImGui.TextDisabled("  Listener invocations: (enable tracking)");

                    if (Engine.Rendering.Stats.EnableRenderMatrixListenerTracking)
                    {
                        var listenerCounts = Engine.Rendering.Stats.GetRenderMatrixListenerSnapshot();
                        if (listenerCounts.Length == 0)
                        {
                            ImGui.TextDisabled("  No listener samples captured yet.");
                        }
                        else
                        {
                            Array.Sort(listenerCounts, (a, b) => b.Value.CompareTo(a.Value));
                            ImGui.Text("  Top listener types:");

                            if (ImGui.BeginTable("RenderMatrixListenerStats", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                            {
                                ImGui.TableSetupColumn("Listener");
                                ImGui.TableSetupColumn("Count");
                                ImGui.TableHeadersRow();

                                int maxRows = Math.Min(listenerCounts.Length, 10);
                                for (int i = 0; i < maxRows; i++)
                                {
                                    ImGui.TableNextRow();
                                    ImGui.TableSetColumnIndex(0); ImGui.Text(listenerCounts[i].Key);
                                    ImGui.TableSetColumnIndex(1); ImGui.Text(listenerCounts[i].Value.ToString());
                                }

                                ImGui.EndTable();
                            }
                        }
                    }
                }

                ImGui.Separator();
                ImGui.Text("Octree Commands:");

                bool enableOctreeStats = Engine.Rendering.Stats.EnableOctreeStats;
                if (ImGui.Checkbox("Enable octree stats", ref enableOctreeStats))
                    Engine.Rendering.Stats.EnableOctreeStats = enableOctreeStats;

                if (!Engine.Rendering.Stats.OctreeStatsReady)
                {
                    ImGui.TextDisabled("  Waiting for stats...");
                }
                else
                {
                    ImGui.Text($"  Add: {Engine.Rendering.Stats.OctreeAddCount:N0}");
                    ImGui.Text($"  Move: {Engine.Rendering.Stats.OctreeMoveCount:N0}");
                    ImGui.Text($"  Remove: {Engine.Rendering.Stats.OctreeRemoveCount:N0}");
                    ImGui.Text($"  Skipped: {Engine.Rendering.Stats.OctreeSkippedMoveCount:N0}");
                }
            }

            if (ImGui.CollapsingHeader("BVH GPU Metrics", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool enableTiming = Engine.Rendering.Settings.EnableGpuBvhTimingQueries;
                if (ImGui.Checkbox("Enable GPU BVH timing queries", ref enableTiming))
                    Engine.Rendering.Settings.EnableGpuBvhTimingQueries = enableTiming;

                var bvhMetrics = Engine.Rendering.BvhStats.Latest;
                ImGui.Text($"Build: {bvhMetrics.BuildCount:N0} items, {bvhMetrics.BuildMilliseconds:F3} ms");
                ImGui.Text($"Refit: {bvhMetrics.RefitCount:N0} nodes, {bvhMetrics.RefitMilliseconds:F3} ms");
                ImGui.Text($"Cull: {bvhMetrics.CullCount:N0} entries, {bvhMetrics.CullMilliseconds:F3} ms");
                ImGui.Text($"Raycasts: {bvhMetrics.RaycastCount:N0} rays, {bvhMetrics.RaycastMilliseconds:F3} ms");
            }

            if (ImGui.CollapsingHeader("Job System", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var jobs = Engine.Jobs;
                ImGui.Text($"Workers: {jobs.WorkerCount}");
                bool bounded = jobs.IsQueueBounded;
                ImGui.Text($"Queue: {(bounded ? "bounded" : "unbounded")}, capacity {jobs.QueueCapacity}, in-use {jobs.QueueSlotsInUse}, available {jobs.QueueSlotsAvailable}");

                if (ImGui.BeginTable("JobSystemStats", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Priority");
                    ImGui.TableSetupColumn("Any");
                    ImGui.TableSetupColumn("Main");
                    ImGui.TableSetupColumn("Collect");
                    ImGui.TableSetupColumn("Avg Wait (ms)");
                    ImGui.TableSetupColumn("Starving?");
                    ImGui.TableHeadersRow();

                    for (int i = 0; i <= (int)JobPriority.Highest; i++)
                    {
                        var priority = (JobPriority)i;
                        int any = jobs.GetQueuedCount(priority, JobAffinity.Any);
                        int main = jobs.GetQueuedCount(priority, JobAffinity.MainThread);
                        int collect = jobs.GetQueuedCount(priority, JobAffinity.CollectVisibleSwap);
                        double waitMs = jobs.GetAverageWait(priority).TotalMilliseconds;
                        bool starving = waitMs >= 2000.0; // mirrors StarvationWarningThreshold

                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0); ImGui.Text(priority.ToString());
                        ImGui.TableSetColumnIndex(1); ImGui.Text(any.ToString());
                        ImGui.TableSetColumnIndex(2); ImGui.Text(main.ToString());
                        ImGui.TableSetColumnIndex(3); ImGui.Text(collect.ToString());
                        ImGui.TableSetColumnIndex(4); ImGui.Text($"{waitMs:F1}");
                        ImGui.TableSetColumnIndex(5);
                        ImGui.TextDisabled(starving ? "yes" : "no");
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.Separator();
            if (ImGui.CollapsingHeader("Main Thread Invokes", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var invokes = Engine.GetMainThreadInvokeLogSnapshot();
                ImGui.Text($"Total invokes: {invokes.Count:N0}");

                if (invokes.Count == 0)
                {
                    ImGui.TextDisabled("No main thread invokes recorded yet.");
                }
                else
                {
                    float rowHeight = ImGui.GetTextLineHeightWithSpacing();
                    float estimatedHeight = MathF.Min(20, invokes.Count) * rowHeight + rowHeight * 2;

                    if (ImGui.BeginTable("ProfilerMainThreadInvokes", 5,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                        new Vector2(-1.0f, estimatedHeight)))
                    {
                        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 60f);
                        ImGui.TableSetupColumn("Timestamp", ImGuiTableColumnFlags.WidthFixed, 185f);
                        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 110f);
                        ImGui.TableSetupColumn("Thread", ImGuiTableColumnFlags.WidthFixed, 60f);
                        ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableHeadersRow();

                        for (int i = 0; i < invokes.Count; i++)
                        {
                            var entry = invokes[i];
                            ImGui.TableNextRow();

                            ImGui.TableSetColumnIndex(0);
                            ImGui.Text(entry.Sequence.ToString());

                            ImGui.TableSetColumnIndex(1);
                            ImGui.Text(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));

                            ImGui.TableSetColumnIndex(2);
                            ImGui.Text(entry.Mode.ToString());

                            ImGui.TableSetColumnIndex(3);
                            ImGui.Text(entry.CallerThreadId.ToString());

                            ImGui.TableSetColumnIndex(4);
                            ImGui.TextWrapped(entry.Reason);
                        }

                        ImGui.EndTable();
                    }
                }
            }

            ImGui.Separator();
            if (ImGui.CollapsingHeader("FPS Drop Spikes", ImGuiTreeNodeFlags.DefaultOpen))
            {
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
                }
                else
                {
                    float rowHeight = ImGui.GetTextLineHeightWithSpacing();
                    float estimatedHeight = MathF.Min(12, _fpsDropSpikePaths.Count) * rowHeight + rowHeight * 2;

                    if (ImGui.BeginTable("ProfilerFpsDropSpikes", 8,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable,
                        new Vector2(-1.0f, estimatedHeight)))
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

                        var sortSpecs = ImGui.TableGetSortSpecs();
                        bool hasSort = sortSpecs.SpecsCount > 0;
                        if (hasSort)
                        {
                            var primary = sortSpecs.Specs;
                            int sortColumn = primary.ColumnIndex;
                            ImGuiSortDirection sortDirection = primary.SortDirection;
                            bool countChanged = _fpsDropSpikeLastSortedCount != _fpsDropSpikePaths.Count;
                            bool sortChanged = sortColumn != _fpsDropSpikeLastSortColumn || sortDirection != _fpsDropSpikeLastSortDirection;

                            if (sortSpecs.SpecsDirty || countChanged || sortChanged)
                            {
                                _fpsDropSpikeSortedIndices.Clear();
                                _fpsDropSpikeSortedIndices.Capacity = Math.Max(_fpsDropSpikeSortedIndices.Capacity, _fpsDropSpikePaths.Count);
                                for (int i = 0; i < _fpsDropSpikePaths.Count; i++)
                                    _fpsDropSpikeSortedIndices.Add(i);

                                if (sortDirection != ImGuiSortDirection.None)
                                {
                                    _fpsDropSpikeSortedIndices.Sort((a, b) => CompareSpikePathRows(_fpsDropSpikePaths[a], _fpsDropSpikePaths[b], sortColumn, sortDirection));
                                }

                                _fpsDropSpikeLastSortedCount = _fpsDropSpikePaths.Count;
                                _fpsDropSpikeLastSortColumn = sortColumn;
                                _fpsDropSpikeLastSortDirection = sortDirection;
                                sortSpecs.SpecsDirty = false;
                            }
                        }

                        if (hasSort && _fpsDropSpikeSortedIndices.Count == _fpsDropSpikePaths.Count)
                        {
                            for (int i = 0; i < _fpsDropSpikeSortedIndices.Count; i++)
                            {
                                var spike = _fpsDropSpikePaths[_fpsDropSpikeSortedIndices[i]];
                                ImGui.TableNextRow();

                            ImGui.TableSetColumnIndex(0);
                            ImGui.Text($"{spike.WorstFrameTimeSeconds:F3}");

                            ImGui.TableSetColumnIndex(1);
                            ImGui.Text(spike.ThreadId.ToString());

                            ImGui.TableSetColumnIndex(2);
                            ImGui.Text(spike.SeenCount.ToString());

                            ImGui.TableSetColumnIndex(3);
                            ImGui.Text($"{spike.WorstComparisonFps:F1}");

                            ImGui.TableSetColumnIndex(4);
                            ImGui.Text($"{spike.WorstCurrentFps:F1}");

                            ImGui.TableSetColumnIndex(5);
                            ImGui.Text($"{spike.WorstDeltaFps:F1}");

                            ImGui.TableSetColumnIndex(6);
                            ImGui.Text($"{spike.WorstDropFraction * 100.0f:F0}%");

                                ImGui.TableSetColumnIndex(7);
                                ImGui.TextWrapped(spike.HotPath);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < _fpsDropSpikePaths.Count; i++)
                            {
                                var spike = _fpsDropSpikePaths[i];
                                ImGui.TableNextRow();

                                ImGui.TableSetColumnIndex(0);
                                ImGui.Text($"{spike.WorstFrameTimeSeconds:F3}");

                                ImGui.TableSetColumnIndex(1);
                                ImGui.Text(spike.ThreadId.ToString());

                                ImGui.TableSetColumnIndex(2);
                                ImGui.Text(spike.SeenCount.ToString());

                                ImGui.TableSetColumnIndex(3);
                                ImGui.Text($"{spike.WorstComparisonFps:F1}");

                                ImGui.TableSetColumnIndex(4);
                                ImGui.Text($"{spike.WorstCurrentFps:F1}");

                                ImGui.TableSetColumnIndex(5);
                                ImGui.Text($"{spike.WorstDeltaFps:F1}");

                                ImGui.TableSetColumnIndex(6);
                                ImGui.Text($"{spike.WorstDropFraction * 100.0f:F0}%");

                                ImGui.TableSetColumnIndex(7);
                                ImGui.TextWrapped(spike.HotPath);
                            }
                        }

                        ImGui.EndTable();
                    }
                }
            }

            ImGui.Checkbox("Sort by Time", ref _profilerSortByTime);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.DragFloat("Update Interval (s)", ref _profilerUpdateIntervalSeconds, 0.05f, 0.1f, 2.0f);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.DragFloat("Persistence (s)", ref _profilerPersistenceSeconds, 0.1f, 0.5f, 10.0f);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.DragFloat("Root Min ms", ref _profilerRootHierarchyMinMs, 0.05f, 0.0f, 1000.0f);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.DragFloat("Root Max ms", ref _profilerRootHierarchyMaxMs, 0.05f, 0.0f, 1000.0f);

            ImGui.Separator();

            // Group graphs by root method name
            if (ImGui.CollapsingHeader("Root Method Graphs", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var rootMethod in _cachedRootMethodGraphList)
                {
                    bool isStale = nowUtc - rootMethod.LastSeen > TimeSpan.FromSeconds(_profilerUpdateIntervalSeconds);
                    float fps = rootMethod.DisplayTotalTimeMs > 0.001f ? 1000.0f / rootMethod.DisplayTotalTimeMs : 0.0f;
                    string headerLabel = $"{rootMethod.Name} ({rootMethod.DisplayTotalTimeMs:F3} ms, {fps:F1} FPS, {rootMethod.ThreadIds.Count} thread(s))";
                    if (isStale)
                        headerLabel += " (inactive)";

                    if (isStale)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));

                    ImGui.Text(headerLabel);

                    if (isStale)
                        ImGui.PopStyleColor();

                    // Plot aggregated samples for this root method
                    if (rootMethod.Samples.Length > 0)
                    {
                        float min = float.MaxValue;
                        float max = float.MinValue;
                        for (int i = 0; i < rootMethod.Samples.Length; i++)
                        {
                            float v = rootMethod.Samples[i];
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                        if (!float.IsFinite(min) || !float.IsFinite(max))
                        {
                            min = 0.0f;
                            max = 0.0f;
                        }
                        if (MathF.Abs(max - min) < 0.001f)
                            max = min + 0.001f;

                        ImGui.PlotLines($"##ProfilerRootMethodPlot_{rootMethod.Name}", ref rootMethod.Samples[0], rootMethod.Samples.Length, 0, null, min, max, new Vector2(-1.0f, 40.0f));
                    }
                }
            }

            ImGui.Separator();
            ImGui.Text("Hierarchy by Root Method");

            if (_cachedRootMethodHierarchyList.Count > 0)
            {
                if (ImGui.BeginTable("ProfilerHierarchy", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60f);
                    ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.WidthFixed, 40f);
                    ImGui.TableHeadersRow();

                    foreach (var rootMethod in _cachedRootMethodHierarchyList)
                    {
                        if (rootMethod.RootNodes.Count > 0)
                        {
                            DrawAggregatedRootMethodHierarchy(rootMethod);
                        }
                    }
                    ImGui.EndTable();
                }
            }
            else
            {
                ImGui.Text("No root methods captured.");
            }
        }

        /// <summary>
        /// Process profiler snapshot directly on UI thread.
        /// Only called when not already processing to avoid double-work.
        /// </summary>
        private static void ProcessProfilerSnapshotInline(
            Engine.CodeProfiler.ProfilerFrameSnapshot snapshot,
            Dictionary<int, float[]>? history,
            DateTime nowUtc)
        {
            history ??= new Dictionary<int, float[]>();
            
            UpdateWorstFrameStatistics(snapshot);
            var snapshotForDisplay = GetSnapshotForHierarchy(snapshot, out float hierarchyFrameMs, out bool showingWorstWindowSample);
            UpdateProfilerThreadCache(snapshot.Threads);
            UpdateRootMethodCache(snapshot, history);
            UpdateFpsDropSpikeLog(snapshot, history);
            UpdateDisplayValues();
            RebuildCachedRootMethodLists();

            _lastProfilerCaptureTime = snapshot.FrameTime;
            _lastProfilerHierarchySnapshot = snapshotForDisplay;
            _lastProfilerHierarchyFrameMs = hierarchyFrameMs;
            _lastProfilerHierarchyUsingWorstWindowSample = showingWorstWindowSample;
            _lastProfilerHistorySnapshot = history;
            _lastProfilerUIUpdate = nowUtc;
        }

        private static void DrawAllocRow(string name, Engine.AllocationRingSnapshot snapshot)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.Text(name);
            ImGui.TableSetColumnIndex(1); ImGui.Text($"{snapshot.LastKB:F2}");
            ImGui.TableSetColumnIndex(2); ImGui.Text($"{snapshot.AverageKB:F2}");
            ImGui.TableSetColumnIndex(3); ImGui.Text($"{snapshot.MaxKB:F2}");
            ImGui.TableSetColumnIndex(4); ImGui.Text($"{snapshot.Samples}/{snapshot.Capacity}");
        }

        private static void UpdateRootMethodCache(Engine.CodeProfiler.ProfilerFrameSnapshot frameSnapshot, Dictionary<int, float[]> history)
        {
            var now = DateTime.UtcNow;
            
            // Reset current frame data but keep structure
            foreach (var entry in _profilerRootMethodCache.Values)
            {
                entry.RootNodes.Clear();
                entry.ThreadIds.Clear();
                entry.TotalTimeMs = 0;
                ResetAggregatedChildrenStats(entry.Children);
            }

            // Aggregate root nodes by method name across all threads
            foreach (var thread in frameSnapshot.Threads)
            {
                foreach (var rootNode in thread.RootNodes)
                {
                    if (!_profilerRootMethodCache.TryGetValue(rootNode.Name, out var aggregate))
                    {
                        aggregate = new ProfilerRootMethodAggregate { Name = rootNode.Name };
                        _profilerRootMethodCache[rootNode.Name] = aggregate;
                    }

                    aggregate.RootNodes.Add(rootNode);
                    aggregate.ThreadIds.Add(thread.ThreadId);
                    aggregate.TotalTimeMs += rootNode.ElapsedMs;
                    aggregate.LastSeen = now;
                    
                    UpdateAggregatedChildrenRecursive(rootNode.Children, aggregate.Children, now);
                }
            }

            // Update accumulated max values for the current window
            foreach (var entry in _profilerRootMethodCache.Values)
            {
                entry.AccumulatedMaxTotalTimeMs = Math.Max(entry.AccumulatedMaxTotalTimeMs, entry.TotalTimeMs);
                UpdateAggregatedChildrenMaxRecursive(entry.Children);
            }

            // Prune stale children
            foreach (var entry in _profilerRootMethodCache.Values)
            {
                PruneAggregatedChildren(entry.Children, now);
            }

            // Build aggregated samples for each root method from thread history
            // We sum up the samples from all threads that have this root method
            foreach (var aggregate in _profilerRootMethodCache.Values)
            {
                if (aggregate.ThreadIds.Count == 0)
                    continue;

                // Get the max sample length from participating threads
                int maxLength = 0;
                foreach (var threadId in aggregate.ThreadIds)
                {
                    if (history.TryGetValue(threadId, out var samples))
                        maxLength = Math.Max(maxLength, samples.Length);
                }

                if (maxLength > 0)
                {
                    if (aggregate.Samples.Length != maxLength)
                        aggregate.Samples = new float[maxLength];
                    else
                        Array.Clear(aggregate.Samples, 0, aggregate.Samples.Length);

                    // Sum samples from all threads (simple aggregation)
                    foreach (var threadId in aggregate.ThreadIds)
                    {
                        if (history.TryGetValue(threadId, out var samples))
                        {
                            for (int i = 0; i < samples.Length && i < maxLength; i++)
                                aggregate.Samples[i] += samples[i];
                        }
                    }
                }
            }

            // Remove stale entries that haven't been seen recently (only when not paused)
            if (!_profilerPaused)
            {
                _profilerRootMethodKeysToRemove.Clear();
                var staleThreshold = TimeSpan.FromSeconds(_profilerPersistenceSeconds);
                foreach (var kvp in _profilerRootMethodCache)
                {
                    if (now - kvp.Value.LastSeen > staleThreshold)
                        _profilerRootMethodKeysToRemove.Add(kvp.Key);
                }
                foreach (var key in _profilerRootMethodKeysToRemove)
                    _profilerRootMethodCache.Remove(key);
            }
        }

        private static readonly List<string> _profilerRootMethodKeysToRemove = new();
        private static readonly List<string> _profilerChildKeysToRemove = new();

        private static void RebuildCachedRootMethodLists()
        {
            var nowUtc = DateTime.UtcNow;
            float minRootMs = Math.Max(0.0f, _profilerRootHierarchyMinMs);
            float maxRootMs = _profilerRootHierarchyMaxMs;
            bool hasMaxRootMs = maxRootMs > 0.0f;
            var staleThreshold = _profilerPersistenceSeconds;

            // Rebuild graph list
            _cachedRootMethodGraphList.Clear();
            foreach (var rm in _profilerRootMethodCache.Values)
            {
                bool visible = rm.DisplayTotalTimeMs >= 0.1f || _profilerPaused || (nowUtc - rm.LastSeen).TotalSeconds < staleThreshold;
                bool inRange = rm.DisplayTotalTimeMs >= minRootMs && (!hasMaxRootMs || rm.DisplayTotalTimeMs <= maxRootMs);
                if (visible && inRange)
                    _cachedRootMethodGraphList.Add(rm);
            }
            _cachedRootMethodGraphList.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

            // Rebuild hierarchy list and cache sorted children
            _cachedRootMethodHierarchyList.Clear();
            foreach (var rm in _profilerRootMethodCache.Values)
            {
                bool visible = rm.DisplayTotalTimeMs >= 0.1f || _profilerPaused || (nowUtc - rm.LastSeen).TotalSeconds < staleThreshold;
                bool inRange = rm.DisplayTotalTimeMs >= minRootMs && (!hasMaxRootMs || rm.DisplayTotalTimeMs <= maxRootMs);
                if (visible && inRange)
                {
                    _cachedRootMethodHierarchyList.Add(rm);
                    RebuildCachedChildrenRecursive(rm.Children, rm.DisplayTotalTimeMs, out var sortedChildren, out var untrackedTime, nowUtc, staleThreshold);
                    rm.CachedSortedChildren = sortedChildren;
                    rm.CachedUntrackedTime = untrackedTime;
                }
            }

            if (_profilerSortByTime)
                _cachedRootMethodHierarchyList.Sort(static (a, b) => b.DisplayTotalTimeMs.CompareTo(a.DisplayTotalTimeMs));
            else
                _cachedRootMethodHierarchyList.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        private static void RebuildCachedChildrenRecursive(Dictionary<string, AggregatedChildNode> children, float parentTime, out AggregatedChildNode[] sortedChildren, out float untrackedTime, DateTime nowUtc, float staleThreshold)
        {
            float childrenTotalTime = 0f;
            int visibleCount = 0;
            foreach (var c in children.Values)
            {
                childrenTotalTime += c.DisplayTotalElapsedMs;
                if (c.DisplayTotalElapsedMs >= 0.1f || _profilerPaused || (nowUtc - c.LastSeen).TotalSeconds < staleThreshold)
                    visibleCount++;
            }
            untrackedTime = parentTime - childrenTotalTime;

            sortedChildren = visibleCount > 0 ? new AggregatedChildNode[visibleCount] : [];
            int idx = 0;
            foreach (var c in children.Values)
            {
                if (c.DisplayTotalElapsedMs >= 0.1f || _profilerPaused || (nowUtc - c.LastSeen).TotalSeconds < staleThreshold)
                {
                    sortedChildren[idx++] = c;
                    // Recursively rebuild children
                    RebuildCachedChildrenRecursive(c.Children, c.DisplayTotalElapsedMs, out var childSorted, out var childUntracked, nowUtc, staleThreshold);
                    c.CachedSortedChildren = childSorted;
                    c.CachedUntrackedTime = childUntracked;
                }
            }

            if (_profilerSortByTime && sortedChildren.Length > 1)
                Array.Sort(sortedChildren, static (a, b) => b.DisplayTotalElapsedMs.CompareTo(a.DisplayTotalElapsedMs));
        }

        private static void UpdateDisplayValues()
        {
            foreach (var entry in _profilerRootMethodCache.Values)
            {
                entry.DisplayTotalTimeMs = entry.AccumulatedMaxTotalTimeMs;
                entry.AccumulatedMaxTotalTimeMs = 0;
                UpdateAggregatedChildrenDisplayRecursive(entry.Children);
            }
        }

        private static void UpdateAggregatedChildrenDisplayRecursive(Dictionary<string, AggregatedChildNode> children)
        {
            foreach (var child in children.Values)
            {
                child.DisplayTotalElapsedMs = child.AccumulatedMaxTotalElapsedMs;
                child.DisplayCallCount = child.AccumulatedMaxCallCount;
                child.AccumulatedMaxTotalElapsedMs = 0;
                child.AccumulatedMaxCallCount = 0;
                UpdateAggregatedChildrenDisplayRecursive(child.Children);
            }
        }

        private static void UpdateAggregatedChildrenMaxRecursive(Dictionary<string, AggregatedChildNode> children)
        {
            foreach (var child in children.Values)
            {
                child.AccumulatedMaxTotalElapsedMs = Math.Max(child.AccumulatedMaxTotalElapsedMs, child.TotalElapsedMs);
                child.AccumulatedMaxCallCount = Math.Max(child.AccumulatedMaxCallCount, child.CallCount);
                UpdateAggregatedChildrenMaxRecursive(child.Children);
            }
        }

        private static void ResetAggregatedChildrenStats(Dictionary<string, AggregatedChildNode> children)
        {
            foreach (var child in children.Values)
            {
                child.TotalElapsedMs = 0;
                child.CallCount = 0;
                ResetAggregatedChildrenStats(child.Children);
            }
        }

        private static void UpdateAggregatedChildrenRecursive(IReadOnlyList<Engine.CodeProfiler.ProfilerNodeSnapshot> sourceChildren, Dictionary<string, AggregatedChildNode> targetChildren, DateTime now)
        {
            foreach (var child in sourceChildren)
            {
                if (!targetChildren.TryGetValue(child.Name, out var aggChild))
                {
                    aggChild = new AggregatedChildNode { Name = child.Name };
                    targetChildren[child.Name] = aggChild;
                }
                
                aggChild.TotalElapsedMs += child.ElapsedMs;
                aggChild.CallCount++;
                aggChild.LastSeen = now;
                
                UpdateAggregatedChildrenRecursive(child.Children, aggChild.Children, now);
            }
        }

        private static void PruneAggregatedChildren(Dictionary<string, AggregatedChildNode> children, DateTime now)
        {
            // Only prune when profiler is not paused
            if (!_profilerPaused)
            {
                _profilerChildKeysToRemove.Clear();
                var staleThreshold = TimeSpan.FromSeconds(_profilerPersistenceSeconds);
                foreach (var kvp in children)
                {
                    if (now - kvp.Value.LastSeen > staleThreshold)
                        _profilerChildKeysToRemove.Add(kvp.Key);
                }
                foreach (var key in _profilerChildKeysToRemove)
                    children.Remove(key);
            }
                
            foreach (var child in children.Values)
                PruneAggregatedChildren(child.Children, now);
        }

        private static void UpdateFpsDropSpikeLog(Engine.CodeProfiler.ProfilerFrameSnapshot frameSnapshot, Dictionary<int, float[]> history)
        {
            if (!float.IsFinite(frameSnapshot.FrameTime))
                return;

            // Avoid duplicate processing when the UI draws multiple times per captured snapshot.
            if (frameSnapshot.FrameTime == _lastFpsSpikeProcessedFrameTime)
                return;

            _lastFpsSpikeProcessedFrameTime = frameSnapshot.FrameTime;

            foreach (var thread in frameSnapshot.Threads)
            {
                if (!history.TryGetValue(thread.ThreadId, out var samples) || samples.Length < 2)
                    continue;

                float currentMs = samples[^1];
                float previousMs = samples[^2];

                if (currentMs <= 0.0001f || previousMs <= 0.0001f)
                    continue;

                float currentFps = 1000.0f / currentMs;
                float previousFps = 1000.0f / previousMs;

                if (previousFps < FpsSpikeMinPreviousFps)
                    continue;

                if (currentMs - previousMs < FpsSpikeMinDeltaMs)
                    continue;

                float baselineMs = GetMedianTailMs(samples, FpsSpikeBaselineWindowSamples, skipFromEnd: 1);
                if (baselineMs <= 0.0001f)
                    continue;

                float baselineFps = 1000.0f / baselineMs;
                float comparisonFps = MathF.Min(previousFps, baselineFps);
                if (comparisonFps <= 0.0001f)
                    continue;

                float deltaFps = comparisonFps - currentFps;
                if (deltaFps < _fpsSpikeMinDropFps)
                    continue;

                float dropFraction = deltaFps / comparisonFps;
                if (dropFraction < 0.0f)
                    dropFraction = 0.0f;
                if (dropFraction > 1.0f)
                    dropFraction = 1.0f;

                string hotPath = GetHottestPath(thread.RootNodes, out float hotPathMs);
                string key = $"{thread.ThreadId}:{hotPath}";
                var nowUtc = DateTime.UtcNow;
                var candidate = new FpsDropSpikePathEntry(
                    thread.ThreadId,
                    hotPath,
                    1,
                    nowUtc,
                    nowUtc,
                    frameSnapshot.FrameTime,
                    comparisonFps,
                    currentFps,
                    deltaFps,
                    dropFraction);

                if (_fpsDropSpikePathIndexByKey.TryGetValue(key, out int existingIndex))
                {
                    var existing = _fpsDropSpikePaths[existingIndex];
                    int updatedSeenCount = existing.SeenCount + 1;
                    var updated = existing with
                    {
                        SeenCount = updatedSeenCount,
                        LastSeenUtc = nowUtc
                    };

                    if (deltaFps > existing.WorstDeltaFps)
                    {
                        updated = updated with
                        {
                            WorstFrameTimeSeconds = frameSnapshot.FrameTime,
                            WorstComparisonFps = comparisonFps,
                            WorstCurrentFps = currentFps,
                            WorstDeltaFps = deltaFps,
                            WorstDropFraction = dropFraction
                        };
                    }

                    _fpsDropSpikePaths[existingIndex] = updated;
                }
                else
                {
                    _fpsDropSpikePathIndexByKey[key] = _fpsDropSpikePaths.Count;
                    _fpsDropSpikePaths.Add(candidate);
                }
            }
        }

        private static int CompareSpikePathRows(FpsDropSpikePathEntry a, FpsDropSpikePathEntry b, int sortColumn, ImGuiSortDirection direction)
        {
            int sign = direction == ImGuiSortDirection.Descending ? -1 : 1;
            int result;
            switch (sortColumn)
            {
                case 0: // Worst Frame (s)
                    result = a.WorstFrameTimeSeconds.CompareTo(b.WorstFrameTimeSeconds);
                    break;
                case 1: // Thread
                    result = a.ThreadId.CompareTo(b.ThreadId);
                    break;
                case 2: // Count
                    result = a.SeenCount.CompareTo(b.SeenCount);
                    break;
                case 3: // Ref FPS
                    result = a.WorstComparisonFps.CompareTo(b.WorstComparisonFps);
                    break;
                case 4: // Now FPS
                    result = a.WorstCurrentFps.CompareTo(b.WorstCurrentFps);
                    break;
                case 5: // Drop FPS
                    result = a.WorstDeltaFps.CompareTo(b.WorstDeltaFps);
                    break;
                case 6: // Drop %
                    result = a.WorstDropFraction.CompareTo(b.WorstDropFraction);
                    break;
                case 7: // Hot Path
                    result = string.CompareOrdinal(a.HotPath, b.HotPath);
                    break;
                default:
                    result = 0;
                    break;
            }

            if (result == 0)
            {
                // Deterministic tie-breaker.
                result = a.ThreadId != b.ThreadId ? a.ThreadId.CompareTo(b.ThreadId) : string.CompareOrdinal(a.HotPath, b.HotPath);
            }

            return result * sign;
        }

        private static float GetMedianTailMs(float[] samples, int takeCount, int skipFromEnd)
        {
            int available = samples.Length - skipFromEnd;
            if (available <= 0)
                return 0.0f;

            int count = Math.Min(takeCount, available);
            if (count <= 0)
                return 0.0f;

            float[] window = new float[count];
            Array.Copy(samples, available - count, window, 0, count);
            Array.Sort(window);
            int mid = count / 2;
            return (count % 2 == 0) ? (window[mid - 1] + window[mid]) * 0.5f : window[mid];
        }

        private static string GetHottestPath(IReadOnlyList<Engine.CodeProfiler.ProfilerNodeSnapshot> roots, out float pathMs)
        {
            pathMs = 0.0f;
            if (roots.Count == 0)
                return "(no samples)";

            Engine.CodeProfiler.ProfilerNodeSnapshot hottest = roots[0];
            for (int i = 1; i < roots.Count; i++)
            {
                if (roots[i].ElapsedMs > hottest.ElapsedMs)
                    hottest = roots[i];
            }

            pathMs = hottest.ElapsedMs;
            List<string> parts = new(8) { hottest.Name };
            var current = hottest;

            while (current.Children.Count > 0)
            {
                var children = current.Children;
                var best = children[0];
                for (int i = 1; i < children.Count; i++)
                {
                    if (children[i].ElapsedMs > best.ElapsedMs)
                        best = children[i];
                }

                parts.Add(best.Name);
                current = best;
            }

            return string.Join(" > ", parts);
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

        private static void DrawAggregatedRootMethodHierarchy(ProfilerRootMethodAggregate rootMethod)
        {
            // Use cached sorted children from RebuildCachedRootMethodLists
            var children = rootMethod.CachedSortedChildren;
            float untrackedTime = rootMethod.CachedUntrackedTime;
            int allChildrenCount = rootMethod.Children.Count;

            // Draw the root method summary row
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            
            ImGuiTreeNodeFlags rootFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
            if (children.Length == 0 && (untrackedTime < 0.1f || allChildrenCount == 0))
                rootFlags |= ImGuiTreeNodeFlags.Leaf;

            string rootKey = $"Root_{rootMethod.Name}";
            if (_profilerNodeOpenCache.TryGetValue(rootKey, out bool isRootOpen) && isRootOpen)
                rootFlags |= ImGuiTreeNodeFlags.DefaultOpen;

            bool rootNodeOpen = ImGui.TreeNodeEx($"{rootMethod.Name} (aggregated)##{rootKey}", rootFlags);
            if (ImGui.IsItemToggledOpen())
                _profilerNodeOpenCache[rootKey] = rootNodeOpen;

            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"{rootMethod.DisplayTotalTimeMs:F3}");

            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{rootMethod.RootNodes.Count}");

            if (rootNodeOpen)
            {
                // Children are already sorted by RebuildCachedRootMethodLists
                for (int i = 0; i < children.Length; i++)
                    DrawAggregatedChildNode(children[i], rootKey);
                
                if (untrackedTime >= 0.1f && allChildrenCount > 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TreeNodeEx($"(untracked time)##Untracked_{rootKey}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{untrackedTime:F3}");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text("-");
                }

                ImGui.TreePop();
            }
        }

        private static void DrawAggregatedChildNode(AggregatedChildNode node, string idSuffix)
        {
            // Use cached sorted children from RebuildCachedRootMethodLists
            var children = node.CachedSortedChildren;
            float untrackedTime = node.CachedUntrackedTime;
            int allChildrenCount = node.Children.Count;

            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
            if (children.Length == 0 && (untrackedTime < 0.1f || allChildrenCount == 0))
                flags |= ImGuiTreeNodeFlags.Leaf;

            string nodeKey = $"{node.Name}_{idSuffix}";
            if (_profilerNodeOpenCache.TryGetValue(nodeKey, out bool isOpen) && isOpen)
                flags |= ImGuiTreeNodeFlags.DefaultOpen;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            bool nodeOpen = ImGui.TreeNodeEx($"{node.Name}##{nodeKey}", flags);
            if (ImGui.IsItemToggledOpen())
                _profilerNodeOpenCache[nodeKey] = nodeOpen;

            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"{node.DisplayTotalElapsedMs:F3}");

            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{node.DisplayCallCount}");

            if (nodeOpen)
            {
                // Children are already sorted by RebuildCachedRootMethodLists
                for (int i = 0; i < children.Length; i++)
                    DrawAggregatedChildNode(children[i], nodeKey);
                
                if (untrackedTime >= 0.1f && allChildrenCount > 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TreeNodeEx($"(untracked time)##Untracked_{nodeKey}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{untrackedTime:F3}");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text("-");
                }

                ImGui.TreePop();
            }
        }

        // Helper class to aggregate child nodes across multiple root nodes
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

        // Cache entry for root method aggregation
        private sealed class ProfilerRootMethodAggregate
        {
            public string Name { get; set; } = string.Empty;
            public float TotalTimeMs { get; set; }
            
            public float AccumulatedMaxTotalTimeMs { get; set; }
            public float DisplayTotalTimeMs { get; set; }
            public float CachedUntrackedTime { get; set; }

            public HashSet<int> ThreadIds { get; } = new();
            public List<Engine.CodeProfiler.ProfilerNodeSnapshot> RootNodes { get; } = new();
            public float[] Samples { get; set; } = Array.Empty<float>();
            public DateTime LastSeen { get; set; }
            public Dictionary<string, AggregatedChildNode> Children { get; } = new();
            public AggregatedChildNode[] CachedSortedChildren { get; set; } = [];
        }

        private static void UpdateProfilerThreadCache(IReadOnlyList<Engine.CodeProfiler.ProfilerThreadSnapshot> threads)
        {
            var now = DateTime.UtcNow;
            // Mark existing as stale
            foreach (var entry in _profilerThreadCache.Values)
            {
                if (now - entry.LastSeen > ProfilerThreadStaleThreshold)
                    entry.IsStale = true;
            }

            // Update or add
            foreach (var thread in threads)
            {
                if (!_profilerThreadCache.TryGetValue(thread.ThreadId, out var entry))
                {
                    entry = new ProfilerThreadCacheEntry { ThreadId = thread.ThreadId };
                    _profilerThreadCache[thread.ThreadId] = entry;
                }
                entry.LastSeen = now;
                entry.IsStale = false;
                entry.Snapshot = thread;
            }

            // Remove old - avoid LINQ allocation
            _profilerThreadCacheKeysToRemove.Clear();
            foreach (var kvp in _profilerThreadCache)
            {
                if (now - kvp.Value.LastSeen > ProfilerThreadCacheTimeout)
                    _profilerThreadCacheKeysToRemove.Add(kvp.Key);
            }
            foreach (var key in _profilerThreadCacheKeysToRemove)
                _profilerThreadCache.Remove(key);
        }

        private static readonly List<int> _profilerThreadCacheKeysToRemove = new();

        private static Engine.CodeProfiler.ProfilerFrameSnapshot GetSnapshotForHierarchy(Engine.CodeProfiler.ProfilerFrameSnapshot currentSnapshot, out float frameMs, out bool usingWorstSnapshot)
        {
            if (_worstFrameDisplaySnapshot is not null)
            {
                usingWorstSnapshot = true;
                frameMs = _worstFrameDisplayMs;
                return _worstFrameDisplaySnapshot;
            }

            usingWorstSnapshot = false;
            float maxMs = 0f;
            foreach (var t in currentSnapshot.Threads)
            {
                if (t.TotalTimeMs > maxMs)
                    maxMs = t.TotalTimeMs;
            }
            frameMs = maxMs;
            return currentSnapshot;
        }

        private static void UpdateWorstFrameStatistics(Engine.CodeProfiler.ProfilerFrameSnapshot snapshot)
        {
            var now = DateTime.UtcNow;
            if (_worstFrameWindowStart == DateTime.MinValue)
                _worstFrameWindowStart = now;

            float currentFrameMs = snapshot.Threads.Max(t => t.TotalTimeMs);
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
}
