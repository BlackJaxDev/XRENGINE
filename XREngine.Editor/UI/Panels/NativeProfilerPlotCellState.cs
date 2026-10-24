using System.Numerics;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Draws one visible CPU frequency or GPU millisecond root history.</summary>
internal sealed class NativeProfilerPlotCellState
{
    private const int MaximumPoints = 32;
    private const int MaximumTransitions = 64;
    private readonly NativeProfilerRows _rows;
    private readonly UIBoundableTransform _container;
    private readonly UIRetainedPlot _plot;
    private readonly float[] _cpuSamples = new float[NativeProfilerRootHistory.Capacity];
    private readonly double[] _gpuSamples = new double[NativeProfilerRootHistory.Capacity];
    private readonly float[] _points = new float[MaximumPoints];
    private bool _transitioning;
    private bool _hasPendingTransition;
    private NativeProfilerDisplayRow? _pendingRow;
    private bool _retired;
    private NativeProfilerDisplayRow? _boundRow;
    private string? _boundCpuName;
    private string? _boundGpuName;
    private XREngine.Data.Profiling.ProfilerScopeKind _boundCpuKind;
    private int _lastCpuRevision = -1;
    private ulong _lastGpuFrameId;
    private long _lastGpuGeneration;
    private int _lastRequested;
    private float _lastWidth;
    private float _lastHeight;

    public NativeProfilerPlotCellState(SceneNode cell, NativeProfilerRows rows)
    {
        _rows = rows;
        SceneNode container = cell.NewChildWithTransform(out UIBoundableTransform containerBounds, "Root plot");
        _container = containerBounds;
        _plot = new UIRetainedPlot(container, "History");
        Unbind();
    }

    public void Bind(NativeProfilerDisplayRow row)
    {
        if (_retired)
            return;
        QueueTransition(row);
    }

    private void BindCore(NativeProfilerDisplayRow row)
    {
        string? cpuName = row.CpuPlotName;
        string? gpuName = row.GpuPlotName;
        if (cpuName is null && gpuName is null)
        {
            UnbindCore();
            return;
        }
        _boundRow = row;
        bool sameSeries = string.Equals(cpuName, _boundCpuName, StringComparison.Ordinal) &&
            string.Equals(gpuName, _boundGpuName, StringComparison.Ordinal) &&
            row.CpuPlotKind == _boundCpuKind;
        if (!sameSeries)
        {
            _container.Visibility = EVisibility.Hidden;
            if (_hasPendingTransition)
                return;
            _plot.Begin();
            _plot.End();
            if (_hasPendingTransition)
                return;
        }
        float width = _plot.Width;
        float height = _plot.Height;
        if (width <= 0f || height <= 0f)
        {
            _container.Visibility = EVisibility.Visible;
            return;
        }
        int requested = Math.Clamp(Engine.EditorPreferences.Debug.ProfilerPanelGraphSampleCount,
            2, NativeProfilerRootHistory.Capacity);
        if (cpuName is not null && sameSeries && _lastCpuRevision == _rows.RootHistoryRevision &&
            _lastRequested == requested && _lastWidth == width && _lastHeight == height)
        {
            _container.Visibility = EVisibility.Visible;
            return;
        }
        ulong gpuFrameId = 0;
        long gpuGeneration = 0;
        int copied = cpuName is not null
            ? _rows.CopyRootHistory(cpuName, row.CpuPlotKind, _cpuSamples, requested)
            : Math.Min(_gpuSamples.Length, RuntimeEngine.Rendering.Stats.GpuPipelineProfiler
                .CopyGpuRenderPipelineTimingHistory(gpuName!, _gpuSamples,
                    out gpuFrameId, out gpuGeneration));
        if (gpuName is not null && sameSeries && _lastGpuFrameId == gpuFrameId &&
            _lastGpuGeneration == gpuGeneration && _lastRequested == requested &&
            _lastWidth == width && _lastHeight == height)
        {
            _container.Visibility = EVisibility.Visible;
            return;
        }
        _container.Visibility = EVisibility.Hidden;
        if (_hasPendingTransition)
            return;
        _boundCpuName = cpuName;
        _boundGpuName = gpuName;
        _boundCpuKind = row.CpuPlotKind;
        _lastCpuRevision = _rows.RootHistoryRevision;
        _lastGpuFrameId = gpuFrameId;
        _lastGpuGeneration = gpuGeneration;
        _lastRequested = requested;
        _lastWidth = width;
        _lastHeight = height;
        _plot.Begin();
        int count = Math.Min(requested, copied);
        int first = cpuName is not null ? 0 : copied - count;
        if (count < 2)
        {
            _plot.End();
            if (!_hasPendingTransition)
                _container.Visibility = EVisibility.Visible;
            return;
        }
        int points = Math.Min(count, MaximumPoints);
        float maximum = 0f;
        for (int index = 0; index < count; index++)
        {
            float sample = cpuName is not null ? _cpuSamples[index] : (float)_gpuSamples[first + index];
            if (!float.IsFinite(sample) || sample < 0f)
                sample = 0f;
            int bucket = index * points / count;
            if (index == 0 || bucket != (index - 1) * points / count)
                _points[bucket] = sample;
            else
                _points[bucket] = Math.Max(_points[bucket], sample);
        }
        for (int index = 0; index < points; index++)
            maximum = Math.Max(maximum, _points[index]);
        maximum = Math.Max(maximum, 0.001f);
        Vector4 color = cpuName is not null
            ? new(0.48f, 0.9f, 0.5f, 1f) : new(0.32f, 0.7f, 1f, 1f);
        for (int index = 1; index < points; index++)
        {
            _plot.AddLine(new((index - 1) * width / (points - 1),
                    _points[index - 1] * height / maximum),
                new(index * width / (points - 1), _points[index] * height / maximum), color, 1.5f);
            if (_hasPendingTransition)
                return;
        }
        _plot.End();
        if (!_hasPendingTransition)
            _container.Visibility = EVisibility.Visible;
    }

    public void RefreshLayout()
    {
        if (_retired)
            return;
        if (_hasPendingTransition && !_transitioning)
        {
            QueueTransition(_pendingRow);
            return;
        }
        if (_boundRow is not { } row)
            return;
        float width = _plot.Width;
        float height = _plot.Height;
        if (width > 0f && height > 0f && (width != _lastWidth || height != _lastHeight))
            Bind(row);
    }

    public void Unbind()
    {
        QueueTransition(null);
    }

    private void UnbindCore()
    {
        _boundRow = null;
        _boundCpuName = null;
        _boundGpuName = null;
        _lastCpuRevision = -1;
        _lastGpuFrameId = 0;
        _lastGpuGeneration = 0;
        _lastRequested = 0;
        _lastWidth = 0f;
        _lastHeight = 0f;
        _plot.Begin();
        _plot.End();
        if (_hasPendingTransition)
            return;
        _container.Visibility = EVisibility.Collapsed;
    }

    private void QueueTransition(NativeProfilerDisplayRow? row)
    {
        _pendingRow = _retired ? null : row;
        _hasPendingTransition = true;
        if (_transitioning)
        {
            _lastWidth = 0f;
            return;
        }
        _transitioning = true;
        try
        {
            int transitions = 0;
            while (_hasPendingTransition)
            {
                if (++transitions > MaximumTransitions)
                {
                    // Reentrant callbacks cannot keep painting an obsolete series forever.
                    _pendingRow = null;
                    _hasPendingTransition = false;
                    _boundRow = null;
                    _container.Visibility = EVisibility.Collapsed;
                    _plot.Begin();
                    _plot.End();
                    return;
                }
                NativeProfilerDisplayRow? next = _pendingRow;
                _pendingRow = null;
                _hasPendingTransition = false;
                if (next is { } item)
                    BindCore(item);
                else
                    UnbindCore();
            }
        }
        finally
        {
            _transitioning = false;
        }
    }

    public void Retire()
    {
        _retired = true;
        QueueTransition(null);
    }
}
