using System.Globalization;
using XREngine.Rendering;
using FrameStats = XREngine.RuntimeEngine.Rendering.Stats.Frame;
using GpuStats = XREngine.RuntimeEngine.Rendering.Stats.GpuPipelineProfiler;
using MatrixStats = XREngine.RuntimeEngine.Rendering.Stats.RenderMatrix;
using ResourceChurnStats = XREngine.RuntimeEngine.Rendering.Stats.ResourceChurn;
using ShadowAtlasStats = XREngine.RuntimeEngine.Rendering.Stats.ShadowAtlas;
using SpatialStats = XREngine.RuntimeEngine.Rendering.Stats.Octree;
using VramStats = XREngine.RuntimeEngine.Rendering.Stats.Vram;

namespace XREngine.Editor;

public partial class ProfilerPanel
{
    private readonly NativeProfilerSpikeHistory _spikes = new();
    private readonly ResourceChurnStats.SnapshotRow[] _resourceChurnRows = new ResourceChurnStats.SnapshotRow[32];
    private float _minimumSpikeDropFps;
    private bool _fpsFrameLogging;
    internal float MinimumSpikeDropFps
    {
        get => _minimumSpikeDropFps;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _minimumSpikeDropFps, value, publishNotifications: false);
        }
    }
    private readonly List<NativeGpuTimingTraversalFrame> _gpuTraversal = [];
    private readonly GpuPipelineHistoryFrame[] _gpuRecentFrames = new GpuPipelineHistoryFrame[1200];
    private readonly GpuPipelineHistoryFrame[] _gpuWorstFrames = new GpuPipelineHistoryFrame[24];
    private readonly GpuPipelineTimingTreeRow[] _gpuWorstTreeBuffer = new GpuPipelineTimingTreeRow[256];
    private NativeProfilerDisplayRow[] _selectedGpuWorstTree = [];
    private string? _selectedGpuWorstPipeline;
    private long _selectedGpuWorstGeneration;
    private ulong _selectedGpuWorstFrameId;
    private readonly double[] _gpuSortedFrameTimes = new double[1200];
    private bool _fpsViewInitialized;
    private bool _invokesValid;
    private long _lastInvokeRevision;
    private long _lastInvokeSequence;
    private int _lastInvokeCount;

    private bool CaptureSmallDiagnostic(ENativeProfilerView view, List<NativeProfilerDisplayRow> rows)
    {
        switch (view)
        {
            case ENativeProfilerView.BvhMetrics:
                CaptureBvh(rows);
                return true;
            case ENativeProfilerView.JobSystem:
                CaptureJobs(rows);
                return true;
            case ENativeProfilerView.ThreadAllocations:
                CaptureAllocations(rows);
                return true;
            case ENativeProfilerView.MainThreadInvokes:
                return CaptureInvokes(rows);
            case ENativeProfilerView.FpsSpikes:
                return CaptureFpsSpikes(rows);
            case ENativeProfilerView.RenderStats:
                CaptureRenderStats(rows);
                return true;
            case ENativeProfilerView.GpuTimings:
                CaptureGpuTimings(rows);
                return true;
            case ENativeProfilerView.Settings:
                CaptureSettings(rows);
                return true;
            case ENativeProfilerView.RendererState:
                CaptureRendererState(rows);
                return true;
            case ENativeProfilerView.SceneAssets:
                CaptureSceneAssets(rows);
                return true;
            case ENativeProfilerView.FrameLifecycle:
                CaptureFrameLifecycle(rows);
                return true;
            case ENativeProfilerView.FrameOutputs:
                CaptureFrameOutputs(rows);
                return true;
            default:
                return false;
        }
    }

    private static void CaptureBvh(List<NativeProfilerDisplayRow> rows)
    {
        var snapshot = RuntimeEngine.Rendering.BvhStats.Latest;
        rows.Add(new("Build", snapshot.BuildMilliseconds.ToString("F3", CultureInfo.InvariantCulture), $"{snapshot.BuildCount} items"));
        rows.Add(new("Refit", snapshot.RefitMilliseconds.ToString("F3", CultureInfo.InvariantCulture), $"{snapshot.RefitCount} nodes"));
        rows.Add(new("Cull", snapshot.CullMilliseconds.ToString("F3", CultureInfo.InvariantCulture), $"{snapshot.CullCount} entries"));
        rows.Add(new("Raycasts", snapshot.RaycastMilliseconds.ToString("F3", CultureInfo.InvariantCulture), $"{snapshot.RaycastCount} rays"));
        rows.Add(new("Traversal", snapshot.TraversalMilliseconds.ToString("F3", CultureInfo.InvariantCulture), "ms"));
        rows.Add(new("Command emission", snapshot.CommandEmissionMilliseconds.ToString("F3", CultureInfo.InvariantCulture), "ms"));
        rows.Add(new("Command submit", snapshot.CommandEmissionSubmissionMilliseconds.ToString("F3", CultureInfo.InvariantCulture), "ms"));
    }

    private static void CaptureJobs(List<NativeProfilerDisplayRow> rows)
    {
        var jobs = Engine.Jobs;
        rows.Add(new("Workers", jobs.WorkerCount.ToString(CultureInfo.InvariantCulture),
            jobs.IsQueueBounded ? $"Bounded queue, capacity {jobs.QueueCapacity}" : "Unbounded queue"));
        rows.Add(new("Queue slots", jobs.QueueSlotsInUse.ToString(CultureInfo.InvariantCulture),
            $"{jobs.QueueSlotsAvailable} available"));
        for (int index = (int)JobPriority.Lowest; index <= (int)JobPriority.Highest; index++)
        {
            JobPriority priority = (JobPriority)index;
            rows.Add(new(priority.ToString(), jobs.GetAverageWait(priority).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                $"Any {jobs.GetQueuedCount(priority, JobAffinity.Any)} | Main {jobs.GetQueuedCount(priority, JobAffinity.MainThread)} | Collect {jobs.GetQueuedCount(priority, JobAffinity.CollectVisibleSwap)}"));
        }
    }

    private static void CaptureAllocations(List<NativeProfilerDisplayRow> rows)
    {
        if (!Engine.EditorPreferences.Debug.EnableThreadAllocationTracking)
            rows.Add(new("Allocation tracking disabled", string.Empty,
                "Enable tracking in Settings to collect new samples."));
        var snapshot = Engine.Allocations.GetSnapshot();
        AddAllocationRow(rows, "Render", snapshot.Render);
        AddAllocationRow(rows, "Collect+Swap", snapshot.CollectSwap);
        AddAllocationRow(rows, "Update", snapshot.Update);
        AddAllocationRow(rows, "FixedUpdate", snapshot.FixedUpdate);
        int count = Math.Min(snapshot.Scopes.Length, 64);
        for (int index = 0; index < count; index++)
        {
            var scope = snapshot.Scopes[index];
            rows.Add(new(scope.Name, scope.LastKB.ToString("F1", CultureInfo.InvariantCulture),
                $"{scope.Category} | avg {scope.AverageKB:F1} KB | max {scope.MaxKB:F1} KB | over {scope.OverBudgetCount}"));
        }
        if (snapshot.Scopes.Length > count)
            rows.Add(new("Additional scopes", (snapshot.Scopes.Length - count).ToString(CultureInfo.InvariantCulture),
                "Only the first 64 scopes are shown."));
    }

    private static void AddAllocationRow(List<NativeProfilerDisplayRow> rows, string name,
        Engine.AllocationRingSnapshot snapshot)
        => rows.Add(new(name, snapshot.LastKB.ToString("F1", CultureInfo.InvariantCulture),
            $"avg {snapshot.AverageKB:F1} KB | max {snapshot.MaxKB:F1} KB | {snapshot.Samples} samples"));

    private bool CaptureInvokes(List<NativeProfilerDisplayRow> rows)
    {
        long revision = Engine.MainThreadInvokeLogRevision;
        if (_invokesValid && _lastInvokeRevision == revision)
            return false;
        IReadOnlyList<Engine.MainThreadInvokeEntry> entries = Engine.GetMainThreadInvokeLogSnapshot();
        long lastSequence = entries.Count == 0 ? 0 : entries[^1].Sequence;
        if (_invokesValid && _lastInvokeCount == entries.Count && _lastInvokeSequence == lastSequence)
        {
            _lastInvokeRevision = revision;
            return false;
        }
        _invokesValid = true;
        _lastInvokeRevision = revision;
        _lastInvokeCount = entries.Count;
        _lastInvokeSequence = lastSequence;
        rows.Add(new("Total invokes", entries.Count.ToString(CultureInfo.InvariantCulture), string.Empty));
        for (int index = 0; index < entries.Count; index++)
        {
            Engine.MainThreadInvokeEntry entry = entries[index];
            rows.Add(new($"#{entry.Sequence} {entry.Reason}", entry.Mode.ToString(),
                $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} | thread {entry.CallerThreadId}"));
        }
        return true;
    }

    private const string SpikeSortColumnRow = "Sort by";
    private const string SpikeSortOrderRow = "Sort order";

    private static string SpikeSortName(ENativeProfilerSpikeSort column) => column switch
    {
        ENativeProfilerSpikeSort.DropPercent => "Drop (%)",
        ENativeProfilerSpikeSort.Count => "Count",
        ENativeProfilerSpikeSort.WorstFrame => "Worst frame (s)",
        ENativeProfilerSpikeSort.ReferenceFps => "Reference FPS",
        ENativeProfilerSpikeSort.CurrentFps => "Current FPS",
        ENativeProfilerSpikeSort.Thread => "Thread",
        ENativeProfilerSpikeSort.HotPath => "Hot path",
        _ => "Drop FPS",
    };

    /// <summary>Runs the clear or sort command of an activated FPS spike row.</summary>
    /// <returns><see langword="true"/> when the spike rows must be built again.</returns>
    private bool ActivateSpikeRow(string scope)
    {
        switch (scope)
        {
            case "Tracked hot paths":
                return _spikes.Clear();
            case SpikeSortColumnRow:
                ENativeProfilerSpikeSort next = _spikes.SortColumn == ENativeProfilerSpikeSort.HotPath
                    ? ENativeProfilerSpikeSort.DropFps : _spikes.SortColumn + 1;
                // Text and thread columns read best ascending; measured values read best descending.
                return _spikes.SetSort(next, next is not (ENativeProfilerSpikeSort.Thread or ENativeProfilerSpikeSort.HotPath));
            case SpikeSortOrderRow:
                return _spikes.SetSort(_spikes.SortColumn, !_spikes.SortDescending);
            default:
                return false;
        }
    }

    private bool CaptureFpsSpikes(List<NativeProfilerDisplayRow> rows)
    {
        Engine.Profiler.TryGetSnapshot(out var frame, out var history);
        bool changed = _spikes.Record(frame, history, Engine.Profiler.FpsDropMinPreviousFps,
            Engine.Profiler.FpsDropMinDeltaMs, Engine.Profiler.FpsDropBaselineWindowSamples,
            MinimumSpikeDropFps);
        bool logging = Engine.Profiler.EnableFrameLogging;
        if (logging != _fpsFrameLogging)
            changed = true;
        _fpsFrameLogging = logging;
        if (!changed && _fpsViewInitialized)
            return false;
        _fpsViewInitialized = true;
        if (!logging)
            rows.Add(new("Frame logging disabled", string.Empty,
                "Enable frame logging in Settings to collect new spikes."));
        rows.Add(new("Tracked hot paths", _spikes.Count.ToString(CultureInfo.InvariantCulture),
            "Activate this row to clear native spike history."));
        rows.Add(new(SpikeSortColumnRow, SpikeSortName(_spikes.SortColumn),
            "Activate this row to sort by the next column."));
        rows.Add(new(SpikeSortOrderRow, _spikes.SortDescending ? "Descending" : "Ascending",
            "Activate this row to reverse the order."));
        for (int index = 0; index < _spikes.Count; index++)
        {
            var spike = _spikes[index];
            rows.Add(new($"Thread {spike.ThreadId} | {spike.HotPath}",
                spike.WorstDropFps.ToString("F1", CultureInfo.InvariantCulture),
                $"drop {NativeProfilerSpikeHistory.DropPercent(spike).ToString("F1", CultureInfo.InvariantCulture)}% | {spike.SeenCount} samples | {spike.WorstReferenceFps.ToString("F1", CultureInfo.InvariantCulture)} to {spike.WorstCurrentFps.ToString("F1", CultureInfo.InvariantCulture)} FPS | frame {spike.WorstFrameSeconds.ToString("F3", CultureInfo.InvariantCulture)} s"));
        }
        if (_spikes.LastSkippedThreads != 0)
            rows.Add(new("Threads not inspected", _spikes.LastSkippedThreads.ToString(CultureInfo.InvariantCulture),
                "Native spike sampling inspects at most 64 threads per frame."));
        return true;
    }

    private void CaptureRenderStats(List<NativeProfilerDisplayRow> rows)
    {
        if (!Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking)
            rows.Add(new("Render tracking disabled", string.Empty,
                "Enable tracking in Settings. Last-frame counters can be empty or stale."));
        rows.Add(new("Draw calls", FrameStats.DrawCalls.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        rows.Add(new("Multi-draw calls", FrameStats.MultiDrawCalls.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        rows.Add(new("Triangles", FrameStats.TrianglesRendered.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        rows.Add(new("GPU/CPU fallback", RuntimeEngine.Rendering.Stats.GpuFallback.GpuCpuFallbackEvents.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        rows.Add(new("Forbidden fallback", RuntimeEngine.Rendering.Stats.GpuFallback.ForbiddenGpuFallbackEvents.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        rows.Add(new("Mapped buffers", RuntimeEngine.Rendering.Stats.GpuReadback.GpuMappedBuffers.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        rows.Add(new("Readback bytes", RuntimeEngine.Rendering.Stats.GpuReadback.GpuReadbackBytes.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        rows.Add(new("Visible renderers", RuntimeEngine.Rendering.Stats.SceneAssets.VisibleRendererCount.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        rows.Add(new("Visible triangles", RuntimeEngine.Rendering.Stats.SceneAssets.VisibleTriangleCount.ToString(CultureInfo.InvariantCulture), "Last render frame"));
        CaptureRenderMemory(rows);
        CaptureRenderMatrix(rows);
        CaptureSpatialTree(rows);
        CaptureResourceChurn(rows);
        CaptureShadowAtlasSolve(rows);
        CaptureTransparencyDomains(rows);
        CaptureGpuMeshlets(rows);
        CaptureOpenXr(rows);
        CaptureSkinnedBounds(rows);
        CaptureVulkanFrameDiagnostics(rows);
    }

    private void CaptureResourceChurn(List<NativeProfilerDisplayRow> rows)
    {
        int available = ResourceChurnStats.CopyLastFrameRows(_resourceChurnRows,
            out _, out ResourceChurnStats.SnapshotCounts counts, out int omitted);
        if (counts.Created + counts.Recreated + counts.Resized + counts.Destroyed == 0 && available == 0)
            return;
        rows.Add(new("Render resource churn", counts.Created.ToString(CultureInfo.InvariantCulture),
            $"created | recreated {counts.Recreated} | resized {counts.Resized} | destroyed {counts.Destroyed}"));
        int displayed = Math.Min(available, _resourceChurnRows.Length);
        for (int index = 0; index < displayed; index++)
        {
            ResourceChurnStats.SnapshotRow item = _resourceChurnRows[index];
            rows.Add(new(item.ResourceName, item.Count.ToString(CultureInfo.InvariantCulture),
                $"{item.ResourceKind} | {item.EventName} | {(item.Reason.Length == 0 ? "-" : item.Reason)}"));
        }
        int hidden = omitted + available - displayed;
        if (hidden > 0)
            rows.Add(new("Additional resource events", hidden.ToString(CultureInfo.InvariantCulture),
                "Distinct event keys beyond the bounded last-frame list."));
    }

    private static void CaptureShadowAtlasSolve(List<NativeProfilerDisplayRow> rows)
    {
        var solve = ShadowAtlasStats.LastSolveDiagnostics;
        if (solve.ClassifiedRequestCount == 0 && solve.ElapsedMilliseconds <= 0.0)
            return;
        rows.Add(new("Shadow atlas solve", solve.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"ms | {solve.ClassifiedRequestCount} requests | directional {solve.DirectionalRequestCount} | spot {solve.SpotRequestCount} | point {solve.PointRequestCount}"));
        rows.Add(new("Shadow formats", solve.DepthRequestCount.ToString(CultureInfo.InvariantCulture),
            $"depth | variance2 {solve.Variance2RequestCount} | exponential2 {solve.ExponentialVariance2RequestCount} | exponential4 {solve.ExponentialVariance4RequestCount}"));
        rows.Add(new("Shadow solve attempts", solve.BalancedSolveAttemptCount.ToString(CultureInfo.InvariantCulture),
            $"failures {solve.FailedCandidateCount} | demotions {solve.DemotionCount} | sticky {solve.StickyDemotionCount} | grouped {solve.DirectionalGroupDemotionCount} | fallback {solve.DeterministicFallbackDemotionCount} | waterline {solve.WaterlineDemotionCount}"));
        rows.Add(new("Shadow reuse", solve.PriorReserveHitCount.ToString(CultureInfo.InvariantCulture),
            $"reserve hits/misses {solve.PriorReserveHitCount}/{solve.PriorReserveMissCount} | sub-block {solve.PriorSubBlockHitCount}/{solve.PriorSubBlockMissCount} | incremental {solve.IncrementalReuseCount}"));
        rows.Add(new("Shadow pages", solve.PageAllocationSuccessCount.ToString(CultureInfo.InvariantCulture),
            $"allocations {solve.PageAllocationSuccessCount}/{solve.PageAllocationAttemptCount} | creates {solve.PageCreateSuccessCount}/{solve.PageCreateAttemptCount} | clears {solve.PageClearCount}"));
        rows.Add(new("Shadow groups", solve.DirectionalGroupSeedCount.ToString(CultureInfo.InvariantCulture),
            $"directional seeds/members/misses {solve.DirectionalGroupSeedCount}/{solve.DirectionalGroupMemberCount}/{solve.DirectionalGroupCoLocationFailureCount} | point {solve.PointGroupSeedCount}/{solve.PointGroupMemberCount}/{solve.PointGroupCoLocationFailureCount}"));
        if (solve.LastFailureReason != XREngine.Rendering.Shadows.SkipReason.None)
            rows.Add(new("Shadow last failure", solve.LastFailureReason.ToString(), string.Empty));
    }

    private static void CaptureRenderMemory(List<NativeProfilerDisplayRow> rows)
    {
        const double bytesPerMiB = 1024.0 * 1024.0;
        rows.Add(new("GPU memory", (VramStats.AllocatedVRAMBytes / bytesPerMiB).ToString("F2", CultureInfo.InvariantCulture),
            "MiB currently allocated"));
        rows.Add(new("GPU buffer memory", (VramStats.AllocatedBufferBytes / bytesPerMiB).ToString("F2", CultureInfo.InvariantCulture), "MiB"));
        rows.Add(new("GPU texture memory", (VramStats.AllocatedTextureBytes / bytesPerMiB).ToString("F2", CultureInfo.InvariantCulture), "MiB"));
        rows.Add(new("GPU renderbuffer memory", (VramStats.AllocatedRenderBufferBytes / bytesPerMiB).ToString("F2", CultureInfo.InvariantCulture), "MiB"));
        rows.Add(new("FBO write bandwidth", (VramStats.FBOBandwidthBytes / bytesPerMiB).ToString("F2", CultureInfo.InvariantCulture),
            "MiB in the last render frame"));
        AddCount(rows, "FBO binds", VramStats.FBOBindCount);
    }

    private static void CaptureRenderMatrix(List<NativeProfilerDisplayRow> rows)
    {
        if (!MatrixStats.RenderMatrixStatsReady)
        {
            rows.Add(new("Render matrix updates", "Waiting", "No completed swap sample is available."));
            return;
        }
        AddCount(rows, "Render matrices applied", MatrixStats.RenderMatrixApplied);
        AddCount(rows, "Render matrix batches", MatrixStats.RenderMatrixBatchCount);
        AddCount(rows, "Largest matrix batch", MatrixStats.RenderMatrixMaxBatchSize);
        AddCount(rows, "SetRenderMatrix calls", MatrixStats.RenderMatrixSetCalls);
        AddCount(rows, "Matrix listener calls", MatrixStats.RenderMatrixListenerInvocations);
    }

    private static void CaptureSpatialTree(List<NativeProfilerDisplayRow> rows)
    {
        if (!SpatialStats.OctreeStatsReady)
        {
            rows.Add(new("CPU spatial tree", "Waiting", "No completed spatial-tree sample is available."));
            return;
        }
        rows.Add(new("CPU spatial tree", SpatialStats.CpuSpatialTreeMode, "Last completed frame"));
        rows.Add(new("Spatial collect time", SpatialStats.CpuSpatialTreeCollectMs.ToString("F3", CultureInfo.InvariantCulture),
            $"max {SpatialStats.CpuSpatialTreeMaxCollectMs.ToString("F3", CultureInfo.InvariantCulture)} ms"));
        AddCount(rows, "Spatial nodes", SpatialStats.CpuSpatialTreeNodeCount);
        AddCount(rows, "Spatial items", SpatialStats.CpuSpatialTreeItemCount);
        rows.Add(new("Root / unbounded items", SpatialStats.CpuSpatialTreeRootItemCount.ToString(CultureInfo.InvariantCulture),
            SpatialStats.CpuSpatialTreeUnboundedItemCount.ToString(CultureInfo.InvariantCulture)));
        rows.Add(new("Max items / node", SpatialStats.CpuSpatialTreeMaxNodeItemCount.ToString(CultureInfo.InvariantCulture),
            $"depth {SpatialStats.CpuSpatialTreeMaxDepth}"));
        AddCount(rows, "Spatial collect calls", SpatialStats.OctreeCollectCallCount);
        AddCount(rows, "Spatial visible renderables", SpatialStats.OctreeVisibleRenderableCount);
        AddCount(rows, "Spatial emitted commands", SpatialStats.OctreeEmittedCommandCount);
        rows.Add(new("Spatial swap", SpatialStats.OctreeSwapExecutedCommandCount.ToString(CultureInfo.InvariantCulture),
            $"drained {SpatialStats.OctreeSwapDrainedCommandCount} | buffered {SpatialStats.OctreeSwapBufferedCommandCount} | {SpatialStats.OctreeSwapExecuteMs.ToString("F3", CultureInfo.InvariantCulture)} ms"));
        rows.Add(new("Spatial raycasts", SpatialStats.OctreeRaycastProcessedCommandCount.ToString(CultureInfo.InvariantCulture),
            $"dropped {SpatialStats.OctreeRaycastDroppedCommandCount} | traversal {SpatialStats.OctreeRaycastTraversalMs.ToString("F3", CultureInfo.InvariantCulture)} ms"));
    }

    private void CaptureGpuTimings(List<NativeProfilerDisplayRow> rows)
    {
        if (_gpuDumpStatus.Length != 0)
            rows.Add(new("Timing dump", string.Empty, _gpuDumpStatus));
        string backend = GpuStats.GpuRenderPipelineBackend;
        if (!GpuStats.GpuRenderPipelineProfilingEnabled)
        {
            rows.Add(new("GPU timing disabled", backend, GpuStats.GpuRenderPipelineStatusMessage));
            return;
        }
        if (!GpuStats.GpuRenderPipelineProfilingSupported || !GpuStats.GpuRenderPipelineTimingsReady)
        {
            rows.Add(new(GpuStats.GpuRenderPipelineProfilingSupported ? "Waiting for GPU timing" : "GPU timing unsupported",
                backend, GpuStats.GpuRenderPipelineStatusMessage));
            return;
        }
        rows.Add(new("GPU frame", GpuStats.GpuRenderPipelineFrameMs.ToString("F3", CultureInfo.InvariantCulture), backend));
        var roots = GpuStats.GetGpuRenderPipelineTimingRoots();
        bool selectedPresent = false;
        for (int index = 0; index < roots.Length; index++)
        {
            if (!string.Equals(_selectedGpuPipeline, roots[index].Name, StringComparison.Ordinal))
                continue;
            selectedPresent = true;
            break;
        }
        if (!selectedPresent)
        {
            _selectedGpuPipeline = roots.Length == 0 ? null : roots[0].Name;
            ClearSelectedGpuWorstTree();
            ResetTrend(ENativeProfilerView.GpuTimings);
        }
        rows.Add(new("History plot", _selectedGpuPipeline ?? (roots.Length > 0 ? roots[0].Name : "Waiting"),
            "Activate a root timing row to select its captured history."));
        for (int index = 0; index < roots.Length; index++)
        {
            var root = roots[index];
            rows.Add(new($"Root history: {root.Name}",
                root.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture),
                "GPU ms; activate to inspect this root.",
                PipelineName: root.Name, GpuPlotName: root.Name));
        }
        CaptureGpuHistoryDetails(rows, roots);
        _gpuTraversal.Clear();
        _gpuTraversal.Add(new(roots, 0, 0));
        int visited = 0;
        while (_gpuTraversal.Count != 0 && visited < 256)
        {
            int top = _gpuTraversal.Count - 1;
            NativeGpuTimingTraversalFrame frame = _gpuTraversal[top];
            if (frame.NextIndex >= frame.Nodes.Length)
            {
                _gpuTraversal.RemoveAt(top);
                continue;
            }
            var node = frame.Nodes[frame.NextIndex];
            _gpuTraversal[top] = frame with { NextIndex = frame.NextIndex + 1 };
            rows.Add(new($"{new string(' ', Math.Min(frame.Depth * 2, 24))}{node.Name}",
                node.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture),
                $"{node.SampleCount} samples | depth {frame.Depth}",
                PipelineName: frame.Depth == 0 ? node.Name : null));
            visited++;
            if (node.Children.Length != 0)
                _gpuTraversal.Add(new(node.Children, 0, frame.Depth + 1));
        }
        if (_gpuTraversal.Count != 0)
            rows.Add(new("Additional GPU stages", string.Empty,
                "This view shows the first 256 timing stages. Use a timing dump for the full tree."));
        _gpuTraversal.Clear();
    }

    private void CaptureGpuHistoryDetails(List<NativeProfilerDisplayRow> rows,
        XREngine.Data.Profiling.GpuPipelineTimingNodeData[] roots)
    {
        string? pipeline = _selectedGpuPipeline ?? (roots.Length == 0 ? null : roots[0].Name);
        if (pipeline is null || !GpuStats.TryCopyGpuRenderPipelineHistoryDetails(pipeline,
            _gpuRecentFrames, _gpuWorstFrames, out GpuPipelineHistorySummary summary))
            return;
        rows.Add(new($"History: {pipeline}", summary.FrameCount.ToString(CultureInfo.InvariantCulture),
            $"{summary.RetainedFrameCount} retained | {summary.TimerSampleCount} timer samples"));
        rows.Add(new("Average / standard deviation", summary.AverageMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"{summary.StandardDeviationMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} ms deviation"));
        rows.Add(new("Best / worst", summary.MinimumMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"frame {summary.MinimumFrameId} | worst {summary.MaximumMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} ms at frame {summary.MaximumFrameId}"));
        int count = Math.Min(summary.RetainedFrameCount, _gpuRecentFrames.Length);
        for (int index = 0; index < count; index++)
            _gpuSortedFrameTimes[index] = _gpuRecentFrames[index].PipelineMilliseconds;
        if (count != 0)
        {
            Array.Sort(_gpuSortedFrameTimes, 0, count);
            rows.Add(new("Retained p50 / p90", Percentile(_gpuSortedFrameTimes, count, 0.50).ToString("F3", CultureInfo.InvariantCulture),
                $"p90 {Percentile(_gpuSortedFrameTimes, count, 0.90).ToString("F3", CultureInfo.InvariantCulture)} ms"));
            rows.Add(new("Retained p95 / p99", Percentile(_gpuSortedFrameTimes, count, 0.95).ToString("F3", CultureInfo.InvariantCulture),
                $"p99 {Percentile(_gpuSortedFrameTimes, count, 0.99).ToString("F3", CultureInfo.InvariantCulture)} ms"));
            AddGpuWindowStatistics(rows, "After first 10 retained", count > 10 ? 10 : count, count);
            AddGpuWindowStatistics(rows, "After first 50 retained", count > 50 ? 50 : count, count);
            AddGpuWindowStatistics(rows, "Last 300 retained", Math.Max(0, count - 300), count);
        }
        int worstCount = Math.Min(summary.WorstFrameCount, _gpuWorstFrames.Length);
        bool selectedFrameRetained = false;
        for (int index = 0; index < worstCount; index++)
        {
            GpuPipelineHistoryFrame frame = _gpuWorstFrames[index];
            selectedFrameRetained |= frame.FrameId == _selectedGpuWorstFrameId &&
                summary.HistoryGeneration == _selectedGpuWorstGeneration &&
                string.Equals(pipeline, _selectedGpuWorstPipeline, StringComparison.Ordinal);
            rows.Add(new($"Worst #{index + 1} | frame {frame.FrameId}",
                frame.PipelineMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                $"{frame.Timestamp:yyyy-MM-dd HH:mm:ss.fff} | render thread {frame.RenderThreadMilliseconds:F3} ms | unattributed {frame.RenderThreadUnattributedMilliseconds:F3} ms | {frame.TimerSampleCount} samples | {frame.HottestPath} {frame.HottestPathMilliseconds:F3} ms",
                PipelineName: pipeline, GpuWorstFrameId: frame.FrameId,
                GpuHistoryGeneration: summary.HistoryGeneration));
        }
        if (!selectedFrameRetained)
            ClearSelectedGpuWorstTree();
        else
        {
            rows.Add(new($"Worst frame {_selectedGpuWorstFrameId} stages", string.Empty,
                "Exact retained frame; activate another worst-frame row to inspect it."));
            rows.AddRange(_selectedGpuWorstTree);
        }
        CaptureGpuRanks(rows, pipeline, summary.HistoryGeneration);
    }

    private void ClearSelectedGpuWorstTree()
    {
        _selectedGpuWorstPipeline = null;
        _selectedGpuWorstGeneration = 0;
        _selectedGpuWorstFrameId = 0;
        _selectedGpuWorstTree = [];
    }

    private void SelectGpuWorstFrame(ulong rowId, int rowIndex, NativeProfilerDisplayRow row)
    {
        if (row.PipelineName is not { } pipeline || row.GpuHistoryGeneration <= 0 ||
            !GpuStats.TryCopyGpuWorstFrameTree(pipeline, row.GpuHistoryGeneration,
                row.GpuWorstFrameId, _gpuWorstTreeBuffer, out int copied, out bool truncated))
            return;
        if (!IsProfilerOwnerCurrent || _rows.View != ENativeProfilerView.GpuTimings ||
            !_rows.TryGetIndexOfItemId(rowId, out int currentIndex) || currentIndex != rowIndex ||
            _rows.GetItem(currentIndex) != row)
            return;
        NativeProfilerDisplayRow[] formatted = new NativeProfilerDisplayRow[copied + (truncated ? 1 : 0)];
        for (int index = 0; index < copied; index++)
        {
            GpuPipelineTimingTreeRow node = _gpuWorstTreeBuffer[index];
            formatted[index] = new($"{new string(' ', Math.Min(node.Depth * 2, 24))}{node.Name}",
                node.Milliseconds.ToString("F3", CultureInfo.InvariantCulture),
                $"{node.TimerSampleCount} samples | depth {node.Depth}");
        }
        if (truncated)
            formatted[^1] = new("Additional worst-frame stages", string.Empty,
                "Only the first 256 stages are shown; use Dump GPU for the full captured tree.");
        _selectedGpuWorstPipeline = pipeline;
        _selectedGpuWorstGeneration = row.GpuHistoryGeneration;
        _selectedGpuWorstFrameId = row.GpuWorstFrameId;
        _selectedGpuWorstTree = formatted;
        int previousCount = _rows.Count;
        if (CaptureSelectedView())
            SignalRowsChanged(previousCount);
    }

    private void AddGpuWindowStatistics(List<NativeProfilerDisplayRow> rows,
        string name, int start, int end)
    {
        int count = end - start;
        if (count == 0)
            return;
        double sum = 0;
        for (int index = 0; index < count; index++)
        {
            double milliseconds = _gpuRecentFrames[start + index].PipelineMilliseconds;
            _gpuSortedFrameTimes[index] = milliseconds;
            sum += milliseconds;
        }
        Array.Sort(_gpuSortedFrameTimes, 0, count);
        rows.Add(new(name, (sum / count).ToString("F3", CultureInfo.InvariantCulture),
            $"{count} frames | p95 {Percentile(_gpuSortedFrameTimes, count, 0.95).ToString("F3", CultureInfo.InvariantCulture)} ms | max {_gpuSortedFrameTimes[count - 1].ToString("F3", CultureInfo.InvariantCulture)} ms"));
    }

    private static double Percentile(double[] sorted, int count, double quantile)
    {
        double position = (count - 1) * quantile;
        int low = (int)position;
        int high = Math.Min(low + 1, count - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }

    private void CaptureSettings(List<NativeProfilerDisplayRow> rows)
    {
        if (_settingsOwner is not { IsCurrent: true })
        {
            _settingsOwner = new NativeProfilerSettingOwner();
            _settingsOwnerRevision++;
        }
        NativeProfilerSettingOwner owner = _settingsOwner!;
        var debug = owner.EffectiveDebug;
        rows.Add(new("Pause native view", _paused ? "On" : "Off", "Activate to toggle.", (int)ENativeProfilerSetting.Pause));
        rows.Add(new("Component timing", debug.EnableProfilerComponentTiming ? "On" : "Off", "Activate to toggle capture.", (int)ENativeProfilerSetting.ComponentTiming));
        rows.Add(new("Frame logging", debug.EnableProfilerFrameLogging ? "On" : "Off", "Activate to toggle capture.", (int)ENativeProfilerSetting.FrameLogging));
        rows.Add(new("Render statistics", debug.EnableRenderStatisticsTracking ? "On" : "Off", "Activate to toggle tracking.", (int)ENativeProfilerSetting.RenderStatsTracking));
        rows.Add(new("Allocation tracking", debug.EnableThreadAllocationTracking ? "On" : "Off", "Activate to toggle tracking.", (int)ENativeProfilerSetting.AllocationTracking));
        rows.Add(new("Invoke diagnostics", debug.EnableMainThreadInvokeDiagnostics ? "On" : "Off", "Activate to toggle verbose logging.", (int)ENativeProfilerSetting.InvokeDiagnostics));
        rows.Add(new("GPU timing", debug.EnableGpuRenderPipelineProfiling ? "On" : "Off", "Activate to toggle collection.", (int)ENativeProfilerSetting.GpuTiming));
        rows.Add(new("UDP sending", debug.EnableProfilerUdpSending ? "On" : "Off", "Activate to stream to the external profiler.", (int)ENativeProfilerSetting.UdpSending));
        rows.Add(new("Native update interval", debug.ProfilerPanelUpdateIntervalSeconds.ToString("F2", CultureInfo.InvariantCulture), "seconds; 0 updates every tick", (int)ENativeProfilerSetting.UpdateInterval));
        if (_externalProfilerStatus.Length != 0)
            rows.Add(new("External profiler", string.Empty, _externalProfilerStatus));
        rows.Add(new("Snapshot interval", debug.CodeProfilerSnapshotIntervalMs.ToString(CultureInfo.InvariantCulture), "ms; edit value", (int)ENativeProfilerSetting.SnapshotInterval));
        rows.Add(new("History capacity", debug.CodeProfilerThreadHistoryCapacity.ToString(CultureInfo.InvariantCulture), "samples per thread; edit value", (int)ENativeProfilerSetting.HistoryCapacity));
        rows.Add(new("Drop threshold", debug.CodeProfilerFpsDropMinDeltaMs.ToString("F1", CultureInfo.InvariantCulture), "ms; edit value", (int)ENativeProfilerSetting.DropThreshold));
        rows.Add(new("Debug output minimum", debug.CodeProfilerDebugOutputMinElapsedMs.ToString("F1", CultureInfo.InvariantCulture), "ms; edit value", (int)ENativeProfilerSetting.DebugOutputMinimum));
        rows.Add(new("Stats thread interval", debug.CodeProfilerStatsThreadIntervalMs.ToString(CultureInfo.InvariantCulture), "ms; edit value", (int)ENativeProfilerSetting.StatsThreadInterval));
        rows.Add(new("Overflow per cycle", debug.CodeProfilerMaxOverflowPerCycle.ToString(CultureInfo.InvariantCulture), "events; edit value", (int)ENativeProfilerSetting.MaxOverflowPerCycle));
        rows.Add(new("Overflow queue", debug.CodeProfilerMaxOverflowQueueSize.ToString(CultureInfo.InvariantCulture), "events; edit value", (int)ENativeProfilerSetting.MaxOverflowQueueSize));
        rows.Add(new("Producer buffer", debug.CodeProfilerProducerBufferCapacity.ToString(CultureInfo.InvariantCulture), "power-of-two capacity; edit value", (int)ENativeProfilerSetting.ProducerBufferCapacity));
        rows.Add(new("FPS baseline window", debug.CodeProfilerFpsDropBaselineWindowSamples.ToString(CultureInfo.InvariantCulture), "samples; edit value", (int)ENativeProfilerSetting.FpsBaselineWindow));
        rows.Add(new("Minimum previous FPS", debug.CodeProfilerFpsDropMinPreviousFps.ToString("F1", CultureInfo.InvariantCulture), "FPS; edit value", (int)ENativeProfilerSetting.FpsMinimumPrevious));
        rows.Add(new("Render stall threshold", debug.CodeProfilerRenderStallThresholdMs.ToString("F1", CultureInfo.InvariantCulture), "ms; edit value", (int)ENativeProfilerSetting.RenderStallThreshold));
        rows.Add(new("Spike min drop", MinimumSpikeDropFps.ToString("F1", CultureInfo.InvariantCulture), "FPS; edit value", (int)ENativeProfilerSetting.SpikeMinimumDrop));
        rows.Add(new("Graph samples", debug.ProfilerPanelGraphSampleCount.ToString(CultureInfo.InvariantCulture), "Up to 720 samples; plot bins retain peaks across 64 points.", (int)ENativeProfilerSetting.GraphSamples));
        rows.Add(new("Graph smoothing", debug.ProfilerPanelSmoothingAlpha.ToString("F2", CultureInfo.InvariantCulture), "0 to 0.95; edit value", (int)ENativeProfilerSetting.GraphSmoothing));
        rows.Add(new("CPU raw line", debug.ProfilerPanelShowCpuTimingRawMsLine ? "On" : "Off", "Activate to toggle.", (int)ENativeProfilerSetting.CpuRawLine));
        rows.Add(new("CPU smoothed line", debug.ProfilerPanelShowCpuTimingSmoothedMsLine ? "On" : "Off", "Activate to toggle.", (int)ENativeProfilerSetting.CpuSmoothedLine));
        rows.Add(new("GPU raw line", debug.ProfilerPanelShowGpuTimingRawMsLine ? "On" : "Off", "Activate to toggle.", (int)ENativeProfilerSetting.GpuRawLine));
        rows.Add(new("GPU smoothed line", debug.ProfilerPanelShowGpuTimingSmoothedMsLine ? "On" : "Off", "Activate to toggle.", (int)ENativeProfilerSetting.GpuSmoothedLine));
        rows.Add(new("Interpolate CPU", debug.ProfilerPanelInterpolateCpuTimingGraphs ? "On" : "Off", "Animate the latest sample.", (int)ENativeProfilerSetting.CpuInterpolation));
        rows.Add(new("Interpolate GPU", debug.ProfilerPanelInterpolateGpuTimingGraphs ? "On" : "Off", "Animate the latest sample.", (int)ENativeProfilerSetting.GpuInterpolation));
        rows.Add(new("CPU display mode", debug.ProfilerPanelCpuTimingDisplayMode.ToString(), "Activate to cycle Latest, Average, Worst.", (int)ENativeProfilerSetting.CpuDisplayMode));
        rows.Add(new("GPU display mode", debug.ProfilerPanelGpuTimingDisplayMode.ToString(), "Activate to cycle Latest, Average, Worst.", (int)ENativeProfilerSetting.GpuDisplayMode));
        AddSpeedProfileRows(rows);
        if (!owner.IsCurrent)
        {
            rows.Clear();
            rows.Add(new("Settings owner changed", string.Empty, "The view will bind to the new owner on its next refresh."));
            _settingsOwner = null;
            _settingsOwnerRevision++;
            return;
        }
        for (int index = 0; index < rows.Count; index++)
            rows[index] = rows[index] with { OwnerRevision = _settingsOwnerRevision };
    }
}
