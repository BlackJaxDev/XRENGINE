using System.Globalization;
using XREngine.Rendering;
using GpuStats = XREngine.RuntimeEngine.Rendering.Stats.GpuPipelineProfiler;

namespace XREngine.Editor;

public partial class ProfilerPanel
{
    private const int GpuRankPageSize = 64;
    private readonly GpuPipelineNodeRankRow[] _gpuRankBuffer = new GpuPipelineNodeRankRow[GpuRankPageSize];
    private NativeProfilerDisplayRow[] _gpuRankDisplay = [];
    private string? _gpuRankPipeline;
    private long _gpuRankGeneration;
    private long _gpuRankRevision;
    private int _gpuRankOffset;
    private int _gpuRankTotal;
    private bool _gpuRankTruncated;
    private EGpuPipelineNodeRankMetric _gpuRankMetric;

    private void ClearGpuRankPage()
    {
        _gpuRankPipeline = null;
        _gpuRankGeneration = 0;
        _gpuRankRevision = 0;
        _gpuRankOffset = 0;
        _gpuRankTotal = 0;
        _gpuRankTruncated = false;
        _gpuRankDisplay = [];
    }

    private void CaptureGpuRanks(List<NativeProfilerDisplayRow> rows,
        string pipeline, long generation)
    {
        if (!string.Equals(_gpuRankPipeline, pipeline, StringComparison.Ordinal) ||
            _gpuRankGeneration != generation)
        {
            ClearGpuRankPage();
            _gpuRankPipeline = pipeline;
            _gpuRankGeneration = generation;
        }
        for (int index = 0; index < 3; index++)
        {
            EGpuPipelineNodeRankMetric metric = (EGpuPipelineNodeRankMetric)index;
            rows.Add(new($"Node rank: {metric}", metric == _gpuRankMetric ? "Selected" : "Activate",
                "Historical command paths from completed GPU frames.",
                PipelineName: pipeline, GpuHistoryGeneration: generation,
                GpuRankMetric: index, GpuRankRevision: _gpuRankRevision));
        }
        if (!GpuStats.TryCopyGpuNodeRankPage(pipeline, generation, 0,
            _gpuRankMetric, _gpuRankOffset, _gpuRankBuffer,
            out long revision, out int total, out int copied, out bool truncated))
        {
            rows.Add(new("Node rank unavailable", string.Empty,
                "Wait for a completed GPU history frame."));
            return;
        }
        if (_gpuRankRevision != revision || _gpuRankDisplay.Length != copied ||
            _gpuRankTotal != total || _gpuRankTruncated != truncated)
        {
            NativeProfilerDisplayRow[] display = new NativeProfilerDisplayRow[copied];
            for (int index = 0; index < copied; index++)
            {
                GpuPipelineNodeRankRow node = _gpuRankBuffer[index];
                display[index] = new(node.Path,
                    RankValue(node, _gpuRankMetric).ToString("F3", CultureInfo.InvariantCulture),
                    $"avg {node.AverageActiveFrameMilliseconds:F3} ms | max {node.MaximumMilliseconds:F3} ms | total {node.TotalMilliseconds:F3} ms | {node.ObservedFrameCount} frames | {node.TimerSampleCount} samples");
            }
            _gpuRankDisplay = display;
            _gpuRankRevision = revision;
            _gpuRankTotal = total;
            _gpuRankTruncated = truncated;
        }
        rows.Add(new($"Ranked nodes {_gpuRankOffset + 1}–{_gpuRankOffset + copied}",
            total.ToString(CultureInfo.InvariantCulture),
            truncated ? "Only the first 256 paths are available; Dump GPU contains all paths." :
                "Values cover the captured history lifetime."));
        rows.AddRange(_gpuRankDisplay);
        if (_gpuRankOffset > 0)
            rows.Add(new("Previous rank page", string.Empty, "Activate to show the prior 64 paths.",
                PipelineName: pipeline, GpuHistoryGeneration: generation,
                GpuRankMetric: (int)_gpuRankMetric, GpuRankPageAction: -1,
                GpuRankRevision: revision));
        if (_gpuRankOffset + copied < Math.Min(total, 256))
            rows.Add(new("Next rank page", string.Empty, "Activate to show the next 64 paths.",
                PipelineName: pipeline, GpuHistoryGeneration: generation,
                GpuRankMetric: (int)_gpuRankMetric, GpuRankPageAction: 1,
                GpuRankRevision: revision));
    }

    private static double RankValue(GpuPipelineNodeRankRow node, EGpuPipelineNodeRankMetric metric)
        => metric switch
        {
            EGpuPipelineNodeRankMetric.AverageActiveFrame => node.AverageActiveFrameMilliseconds,
            EGpuPipelineNodeRankMetric.TotalCapturedTime => node.TotalMilliseconds,
            _ => node.MaximumMilliseconds,
        };

    private void SelectGpuRankPage(ulong rowId, int rowIndex, NativeProfilerDisplayRow row)
    {
        if (row.PipelineName is not { } pipeline || row.GpuHistoryGeneration <= 0 ||
            row.GpuRankMetric is < 0 or > 2 ||
            !string.Equals(_selectedGpuPipeline, pipeline, StringComparison.Ordinal))
            return;
        int offset = row.GpuRankPageAction == 0 ? 0 :
            _gpuRankOffset + row.GpuRankPageAction * GpuRankPageSize;
        if (offset < 0 || offset > 255 ||
            !GpuStats.TryCopyGpuNodeRankPage(pipeline, row.GpuHistoryGeneration,
                row.GpuRankPageAction == 0 ? 0 : row.GpuRankRevision,
                (EGpuPipelineNodeRankMetric)row.GpuRankMetric, offset, _gpuRankBuffer,
                out long revision, out _, out _, out _) ||
            !IsProfilerOwnerCurrent || _rows.View != ENativeProfilerView.GpuTimings ||
            !_rows.TryGetIndexOfItemId(rowId, out int currentIndex) || currentIndex != rowIndex ||
            _rows.GetItem(currentIndex) != row)
            return;
        _gpuRankMetric = (EGpuPipelineNodeRankMetric)row.GpuRankMetric;
        _gpuRankOffset = offset;
        _gpuRankRevision = 0;
        _gpuRankGeneration = row.GpuHistoryGeneration;
        _gpuRankPipeline = pipeline;
        int previousCount = _rows.Count;
        if (CaptureSelectedView())
            SignalRowsChanged(previousCount);
    }
}
