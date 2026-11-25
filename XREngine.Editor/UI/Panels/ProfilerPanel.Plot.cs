using System.Globalization;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Profiling;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class ProfilerPanel
{
    private const int TrendCapacity = 720;
    // Raw and smoothed lines use (points - 1) segments each. With the mode line, three grid lines,
    // four frame-budget guides, and the hover marker, 60 points keep the total within
    // UIRetainedPlot.MaximumSegments.
    private const int RenderPointCapacity = 60;
    private readonly float[] _trend = new float[TrendCapacity];
    private readonly float[] _renderedTrend = new float[RenderPointCapacity];
    private readonly float[] _smoothedTrend = new float[RenderPointCapacity];
    private readonly double[] _gpuHistoryScratch = new double[TrendCapacity];
    private UIRetainedPlot? _trendPlot;
    private UITextComponent? _trendHoverLabel;
    private float _trendHoverX = -1f;
    private int _trendHoverIndex = -1;
    private int _trendHoverVisible;
    private string? _selectedGpuPipeline;
    private ulong _lastGpuHistoryFrameId = ulong.MaxValue;
    private long _lastGpuHistoryGeneration;
    private long _lastCpuPublicationId = long.MinValue;
    private ENativeProfilerView _trendView;
    private int _trendCount;
    private int _trendNext;
    private float _previousNewestValue;
    private DateTime _newestSampleAt;

    protected override float FooterHeight => 76;

    protected override void CreateFooter(SceneNode parent)
    {
        UIRetainedPlot plot = new(parent, "Profiler trend");
        _trendPlot = plot;
        NativePlotHoverComponent hover = plot.Root.AddComponent<NativePlotHoverComponent>()!;
        hover.MouseMove += OnTrendPointerMoved;
        hover.MouseOverlapLeave += OnTrendPointerLeft;
        plot.Root.NewChild<UITextComponent>(out UITextComponent label);
        label.FontSize = 11;
        label.Color = ColorF4.White;
        label.HorizontalAlignment = EHorizontalAlignment.Left;
        label.VerticalAlignment = EVerticalAlignment.Top;
        label.BoundableTransform.Margins = new Vector4(6, 2, 6, 2);
        label.ClipToBounds = true;
        label.BoundableTransform.Visibility = EVisibility.Hidden;
        _trendHoverLabel = label;
    }

    private void OnTrendPointerMoved(float x, float y, UIInteractableComponent _)
    {
        if (_terminal || !_ownerActive || !float.IsFinite(x))
            return;
        _trendHoverX = x;
        if (UpdateTrendHover(force: false) && _trendPlot is { } plot)
            DrawTrend(plot);
    }

    private void OnTrendPointerLeft(UIInteractableComponent _)
    {
        if (_trendHoverX < 0f)
            return;
        _trendHoverX = -1f;
        _trendHoverIndex = -1;
        if (_trendHoverLabel is { } label)
            label.BoundableTransform.Visibility = EVisibility.Hidden;
        if (_trendPlot is { } plot && !_terminal)
            DrawTrend(plot);
    }

    /// <summary>
    /// Shows the sample under the pointer and the window statistics. The text is formatted only when
    /// the hovered sample changes or a new sample arrives, never on animation-only redraws.
    /// </summary>
    /// <returns><see langword="true"/> when the hovered sample changed.</returns>
    private bool UpdateTrendHover(bool force)
    {
        if (_trendHoverX < 0f || _trendHoverLabel is not { } label || _trendPlot is not { } plot)
            return false;
        int visible = Math.Min(_trendCount, Math.Clamp(
            Engine.EditorPreferences.Debug.ProfilerPanelGraphSampleCount, 2, TrendCapacity));
        float width = plot.Width;
        if (visible < 2 || width <= 0f)
        {
            label.BoundableTransform.Visibility = EVisibility.Hidden;
            return false;
        }
        int index = Math.Clamp((int)MathF.Round(_trendHoverX / width * (visible - 1)), 0, visible - 1);
        if (!force && index == _trendHoverIndex && visible == _trendHoverVisible)
            return false;
        _trendHoverIndex = index;
        _trendHoverVisible = visible;
        int first = (_trendNext - visible + TrendCapacity) % TrendCapacity;
        float minimum = float.PositiveInfinity;
        float maximum = 0f;
        float sum = 0f;
        for (int sample = 0; sample < visible; sample++)
        {
            float value = _trend[(first + sample) % TrendCapacity];
            sum += value;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        float hovered = _trend[(first + index) % TrendCapacity];
        label.Text = string.Create(CultureInfo.InvariantCulture,
            $"{hovered:F2} ms | {visible - 1 - index} samples ago | avg {sum / visible:F2} | min {minimum:F2} | max {maximum:F2}");
        label.BoundableTransform.Visibility = EVisibility.Visible;
        return true;
    }

    private void ResetTrend(ENativeProfilerView view)
    {
        _trendView = view;
        _trendCount = 0;
        _trendNext = 0;
        _lastGpuHistoryFrameId = ulong.MaxValue;
        _lastGpuHistoryGeneration = 0;
        _lastCpuPublicationId = long.MinValue;
        _newestSampleAt = default;
        _trendHoverIndex = -1;
        _trendPlot?.Begin();
        _trendPlot?.End();
    }

    private void SampleTrend()
    {
        UIRetainedPlot? plot = _trendPlot;
        if (plot is null || _terminal || !_ownerActive || !IsActiveInHierarchy)
            return;
        if (_trendView != _rows.View)
            ResetTrend(_rows.View);
        float sample;
        if (_rows.View == ENativeProfilerView.GpuTimings)
        {
            if (!RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady)
                return;
            var roots = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GetGpuRenderPipelineTimingRoots();
            if (_selectedGpuPipeline is null && roots.Length != 0)
                _selectedGpuPipeline = roots[0].Name;
            if (_selectedGpuPipeline is null)
                return;
            int total = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler
                .CopyGpuRenderPipelineTimingHistory(_selectedGpuPipeline, _gpuHistoryScratch,
                    out ulong newestFrameId, out long historyGeneration);
            int copied = Math.Min(total, TrendCapacity);
            if (copied == 0 || newestFrameId == _lastGpuHistoryFrameId &&
                historyGeneration == _lastGpuHistoryGeneration)
                return;
            if (historyGeneration != _lastGpuHistoryGeneration)
                _trendCount = 0;
            _previousNewestValue = _trendCount == 0 ? (float)_gpuHistoryScratch[copied - 1]
                : _trend[(_trendNext - 1 + TrendCapacity) % TrendCapacity];
            _lastGpuHistoryFrameId = newestFrameId;
            _lastGpuHistoryGeneration = historyGeneration;
            _trendCount = copied;
            _trendNext = copied % TrendCapacity;
            for (int index = 0; index < copied; index++)
                _trend[index] = (float)_gpuHistoryScratch[index];
            _newestSampleAt = DateTime.UtcNow;
            UpdateTrendHover(force: true);
            DrawTrend(plot);
            return;
        }
        else if (_rows.View is ENativeProfilerView.CpuTimings or ENativeProfilerView.FpsSpikes)
        {
            if (!Engine.Profiler.TryGetSnapshot(out var frame, out _) || frame is null)
                return;
            if (_lastCpuPublicationId == frame.PublicationId)
                return;
            _lastCpuPublicationId = frame.PublicationId;
            sample = frame.FrameTime * 1000f;
        }
        else if (_rows.View == ENativeProfilerView.CpuRoots)
        {
            if (_selectedCpuRootName is null &&
                _rows.TryGetRootAtRow(4, out NativeProfilerRootAggregate? firstRoot) && firstRoot is not null)
            {
                _selectedCpuRootName = firstRoot.Name;
                _selectedCpuRootKind = firstRoot.Kind;
            }
            if (!_rows.TryGetRootSample(_selectedCpuRootName, _selectedCpuRootKind,
                out long publicationId, out sample) || _lastCpuPublicationId == publicationId)
                return;
            _lastCpuPublicationId = publicationId;
        }
        else
            return;
        if (!float.IsFinite(sample) || sample < 0f)
            return;
        _previousNewestValue = _trendCount == 0 ? sample
            : _trend[(_trendNext - 1 + TrendCapacity) % TrendCapacity];
        _trend[_trendNext] = sample;
        _trendNext = (_trendNext + 1) % TrendCapacity;
        _trendCount = Math.Min(_trendCount + 1, TrendCapacity);
        _newestSampleAt = DateTime.UtcNow;
        UpdateTrendHover(force: true);
        DrawTrend(plot);
    }

    private void AnimateTrend()
    {
        if (_trendPlot is null || _trendCount < 2 || _terminal || !_ownerActive ||
            _newestSampleAt == default || !IsActiveInHierarchy)
            return;
        var debug = Engine.EditorPreferences.Debug;
        bool interpolate = _rows.View == ENativeProfilerView.GpuTimings
            ? debug.ProfilerPanelInterpolateGpuTimingGraphs
            : debug.ProfilerPanelInterpolateCpuTimingGraphs;
        if (interpolate && DateTime.UtcNow - _newestSampleAt < TimeSpan.FromMilliseconds(250))
            DrawTrend(_trendPlot);
    }

    private void DrawTrend(UIRetainedPlot plot)
    {
        plot.Begin();
        int visible = Math.Min(_trendCount, Math.Clamp(
            Engine.EditorPreferences.Debug.ProfilerPanelGraphSampleCount, 2, TrendCapacity));
        if (visible < 2 || plot.Width <= 0 || plot.Height <= 0)
        {
            plot.End();
            return;
        }
        var debug = Engine.EditorPreferences.Debug;
        bool gpu = _rows.View == ENativeProfilerView.GpuTimings;
        bool raw = gpu ? debug.ProfilerPanelShowGpuTimingRawMsLine : debug.ProfilerPanelShowCpuTimingRawMsLine;
        bool smooth = gpu ? debug.ProfilerPanelShowGpuTimingSmoothedMsLine : debug.ProfilerPanelShowCpuTimingSmoothedMsLine;
        bool interpolate = gpu ? debug.ProfilerPanelInterpolateGpuTimingGraphs : debug.ProfilerPanelInterpolateCpuTimingGraphs;
        ProfilerTimingDisplayMode mode = gpu ? debug.ProfilerPanelGpuTimingDisplayMode : debug.ProfilerPanelCpuTimingDisplayMode;
        float animation = interpolate && _newestSampleAt != default
            ? Math.Clamp((float)(DateTime.UtcNow - _newestSampleAt).TotalMilliseconds / 250f, 0f, 1f)
            : 1f;
        float smoothing = Math.Clamp(debug.ProfilerPanelSmoothingAlpha, 0f, 0.95f);
        float maximum = 16f;
        int points = Math.Min(visible, RenderPointCapacity);
        int first = (_trendNext - visible + TrendCapacity) % TrendCapacity;
        float smoothed = 0f;
        float sum = 0f;
        float worst = 0f;
        float newest = 0f;
        int previousBucket = -1;
        for (int index = 0; index < visible; index++)
        {
            float value = _trend[(first + index) % TrendCapacity];
            if (index == visible - 1)
                value = _previousNewestValue + (value - _previousNewestValue) * animation;
            smoothed = index == 0 ? value : smoothed * smoothing + value * (1f - smoothing);
            int bucket = index * points / visible;
            if (bucket != previousBucket)
            {
                _renderedTrend[bucket] = value;
                _smoothedTrend[bucket] = smoothed;
                previousBucket = bucket;
            }
            else
            {
                _renderedTrend[bucket] = Math.Max(_renderedTrend[bucket], value);
                _smoothedTrend[bucket] = Math.Max(_smoothedTrend[bucket], smoothed);
            }
            sum += value;
            worst = Math.Max(worst, value);
            maximum = Math.Max(maximum, Math.Max(value, smoothed));
            newest = value;
        }
        float width = plot.Width;
        float height = plot.Height;
        Vector4 color = gpu
            ? new(0.32f, 0.7f, 1f, 1f) : new(0.48f, 0.9f, 0.5f, 1f);
        float selected = mode switch
        {
            ProfilerTimingDisplayMode.Average => sum / visible,
            ProfilerTimingDisplayMode.Worst => worst,
            _ => smooth ? smoothed : newest,
        };
        DrawTrendGuides(plot, width, height, maximum);
        if (_trendHoverIndex >= 0 && _trendHoverVisible == visible)
        {
            float x = _trendHoverIndex * width / (visible - 1);
            plot.AddLine(new(x, 0f), new(x, height), new(1f, 1f, 1f, 0.35f), 1f);
        }
        plot.AddLine(new(0f, selected * height / maximum),
            new(width, selected * height / maximum), new(color.X, color.Y, color.Z, 0.3f), 1f);
        for (int index = 1; index < points; index++)
        {
            float left = _renderedTrend[index - 1];
            float right = _renderedTrend[index];
            float x0 = (index - 1) * width / (points - 1);
            float x1 = index * width / (points - 1);
            if (raw)
                plot.AddLine(new(x0, left * height / maximum),
                    new(x1, right * height / maximum), color);
            if (smooth)
                plot.AddLine(new(x0, _smoothedTrend[index - 1] * height / maximum),
                    new(x1, _smoothedTrend[index] * height / maximum),
                    new(color.X, color.Y, color.Z, 0.52f));
        }
        plot.End();
    }

    /// <summary>
    /// Draws quarter grid lines and the 120, 90, 60, and 30 FPS frame-budget lines that fall inside the
    /// plot range. The trend always shows milliseconds, so the budgets apply to every trend view.
    /// </summary>
    private static void DrawTrendGuides(UIRetainedPlot plot, float width, float height, float maximum)
    {
        Vector4 grid = new(1f, 1f, 1f, 0.08f);
        for (int line = 1; line < 4; line++)
        {
            float y = height * line / 4f;
            plot.AddLine(new(0f, y), new(width, y), grid, 1f);
        }
        DrawBudgetGuide(plot, width, height, maximum, 8.3333f, new(0.55f, 0.92f, 1f, 0.5f));
        DrawBudgetGuide(plot, width, height, maximum, 11.1111f, new(0.42f, 0.88f, 0.88f, 0.5f));
        DrawBudgetGuide(plot, width, height, maximum, 16.6667f, new(0.3f, 0.85f, 0.45f, 0.5f));
        DrawBudgetGuide(plot, width, height, maximum, 33.3333f, new(0.95f, 0.55f, 0.2f, 0.5f));
    }

    private static void DrawBudgetGuide(UIRetainedPlot plot, float width, float height,
        float maximum, float milliseconds, Vector4 color)
    {
        if (milliseconds > maximum)
            return;
        float y = milliseconds * height / maximum;
        plot.AddLine(new(0f, y), new(width, y), color, 1f);
    }
}
