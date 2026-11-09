using System.Globalization;
using System.Text;

namespace XREngine.Rendering;

/// <summary>
/// Connects one <see cref="TextureStreamingWorkController"/> to the render frames of the host. The lane finds the frame
/// boundary itself: the first call in a new render frame closes the previous frame, gives its sample to the controller,
/// and opens the new frame. Callers ask for permission before each item and report the measured cost after it.
/// </summary>
/// <remarks>
/// The lane reads the frame time from the render timestamps and the render-thread busy time from the cumulative frame
/// lifecycle totals. A sample has a known frame time only when exactly one render frame completed between two lane
/// frames; otherwise the controller uses only the cost. Any thread can call the members, but the lane is meant for the
/// render thread. It allocates nothing.
/// </remarks>
internal sealed class TextureStreamingWorkLane
{
    private readonly object _sync = new();
    private readonly TextureStreamingWorkController _controller;
    private readonly TextureStreamingFrameSampler _sampler = new();
    private readonly int _initialItemsPerFrame;
    private readonly Action? _beforeFrameClose;
    private long _frameKey = long.MinValue;
    private long _discontinuityEpoch;
    private int _items;
    private double _workMilliseconds;
    private bool _deferred;
    private long _totalItems;
    private long _totalDeferredFrames;

    /// <summary>Creates a lane.</summary>
    /// <param name="name">The name in diagnostics.</param>
    /// <param name="initialItemsPerFrame">The limit before the first cost measurement (the old fixed limit of the lane).</param>
    /// <param name="beforeFrameClose">
    /// Optional work that runs on the render thread one time before each frame closes, for example a read of completed GPU
    /// timestamps. Give a cached delegate, so the lane allocates nothing.
    /// </param>
    public TextureStreamingWorkLane(string name, int initialItemsPerFrame, Action? beforeFrameClose = null)
    {
        Name = name;
        _initialItemsPerFrame = Math.Max(1, initialItemsPerFrame);
        _beforeFrameClose = beforeFrameClose;
        _controller = new TextureStreamingWorkController(ResolveOptions());
    }

    /// <summary>The name in diagnostics.</summary>
    public string Name { get; }

    /// <summary>The limit of the current frame.</summary>
    public int ItemsThisFrame
    {
        get
        {
            lock (_sync)
            {
                EnsureFrameNoLock();
                return _controller.ItemsThisFrame;
            }
        }
    }

    /// <summary>The number of items that the current frame did.</summary>
    public int ItemsUsedThisFrame
    {
        get
        {
            lock (_sync)
            {
                EnsureFrameNoLock();
                return _items;
            }
        }
    }

    /// <summary>
    /// Asks for one item in the current frame. When the limit is used, it records that work was deferred.
    /// </summary>
    /// <returns>True when the caller can do the item now.</returns>
    public bool TryAcquire()
        => TryAcquire(1, allowFirstOversized: false);

    /// <summary>
    /// Asks for <paramref name="count"/> items in the current frame as one indivisible unit.
    /// </summary>
    /// <param name="count">The number of items in the unit.</param>
    /// <param name="allowFirstOversized">
    /// When true, the first unit of a frame is admitted even when it is larger than the limit, so an indivisible unit
    /// always makes progress. The controller measures its cost and lowers the limit when the frame goes over.
    /// </param>
    public bool TryAcquire(int count, bool allowFirstOversized)
    {
        if (count <= 0)
            return true;

        lock (_sync)
        {
            EnsureFrameNoLock();
            int limit = _controller.ItemsThisFrame;
            bool fits = _items + count <= limit || (allowFirstOversized && _items == 0 && limit > 0);
            if (!fits)
            {
                _deferred = true;
                return false;
            }

            _items += count;
            _totalItems += count;
            return true;
        }
    }

    /// <summary>
    /// Gives back items that <see cref="TryAcquire()"/> took in the current frame when the work did not run, for example
    /// when the backend deferred its admission. Items of an earlier frame are not given back.
    /// </summary>
    public void ReturnItems(int count)
    {
        if (count <= 0)
            return;

        lock (_sync)
        {
            if (_frameKey != RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks)
                return;

            int returned = Math.Min(count, _items);
            _items -= returned;
            _totalItems -= returned;
        }
    }

    /// <summary>
    /// Opens the current frame without asking for an item. A lane that polls each frame calls it, so frames without lane
    /// work also give frame-time samples to the controller.
    /// </summary>
    public void Touch()
    {
        lock (_sync)
            EnsureFrameNoLock();
    }

    /// <summary>Records that ready work waited for a later frame for a reason outside the lane limit.</summary>
    public void MarkDeferred()
    {
        lock (_sync)
        {
            EnsureFrameNoLock();
            _deferred = true;
        }
    }

    /// <summary>Adds the render-thread time of one timed piece of lane work to the current frame, without GC pauses.</summary>
    public void RecordWork(in TextureStreamingWorkTimer timer)
        => RecordWork(timer.ElapsedWorkMilliseconds);

    /// <summary>Adds measured render-thread time of lane work to the current frame.</summary>
    public void RecordWork(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds <= 0.0)
            return;

        lock (_sync)
        {
            EnsureFrameNoLock();
            _workMilliseconds += milliseconds;
        }
    }

    /// <summary>Adds a GPU timestamp measurement of one item, which can resolve some frames after the item.</summary>
    public void RecordGpuItemCost(double milliseconds)
    {
        lock (_sync)
            _controller.ObserveGpuItemCost(milliseconds);
    }

    /// <summary>Writes the controller state as one line.</summary>
    public void AppendDiagnosticSummary(StringBuilder builder)
    {
        lock (_sync)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            builder.Append(Name)
                .Append(": limit=").Append(_controller.ItemsThisFrame.ToString(inv))
                .Append(" rate=").Append(_controller.ItemsPerFrame.ToString("F3", inv))
                .Append(" target=").Append(_controller.TargetItemsPerFrame.ToString("F3", inv))
                .Append(" costMs=").Append(_controller.CostPerItemMilliseconds.ToString("F3", inv))
                .Append(" gpuMs=").Append(_controller.GpuCostPerItemMilliseconds.ToString("F3", inv))
                .Append(" excessMs=").Append(_controller.ExcessCostPerItemMilliseconds.ToString("F3", inv))
                .Append(" budgetMs=").Append(_controller.BudgetMilliseconds.ToString("F2", inv))
                .Append(" headroomMs=").Append(_controller.HeadroomMilliseconds.ToString("F2", inv))
                .Append(" ceiling=").Append(_controller.CeilingItems.ToString(inv))
                .Append(" overruns=").Append(_controller.OverrunCount.ToString(inv))
                .Append(" backoffs=").Append(_controller.BackoffCount.ToString(inv))
                .Append(" discontinuities=").Append(_controller.DiscontinuityCount.ToString(inv))
                .Append(" probes=").Append(_controller.ProbeCount.ToString(inv))
                .Append(" items=").Append(_totalItems.ToString(inv))
                .Append(" deferredFrames=").Append(_totalDeferredFrames.ToString(inv))
                .AppendLine();
        }
    }

    private void EnsureFrameNoLock()
    {
        long frameKey = RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks;
        if (frameKey == _frameKey)
            return;

        _frameKey = frameKey;
        if (_beforeFrameClose is not null && RuntimeRenderingHostServices.FrameTiming.IsRenderThread)
            _beforeFrameClose();

        if (_sampler.TryAdvance(out TextureStreamingClosedFrame closed))
        {
            if (double.IsFinite(closed.IdleFrameMilliseconds))
                _controller.ObserveIdleFrame(closed.IdleFrameMilliseconds, closed.IdleBusyMilliseconds);

            if (_deferred)
                _totalDeferredFrames++;

            _controller.Configure(ResolveOptions());
            _controller.Update(new TextureStreamingWorkSample(
                closed.FrameMilliseconds,
                closed.BusyMilliseconds,
                TextureStreamingFrameSampler.ResolveTargetFrameMilliseconds(),
                _workMilliseconds,
                _items,
                _deferred,
                ResolveHeadroomShare()));
        }

        // A scene discontinuity (for example, a cutscene cut) starts a probe before the new frame uses the limit.
        long epoch = TextureStreamingCapacity.DiscontinuityEpoch;
        if (epoch != _discontinuityEpoch)
        {
            _discontinuityEpoch = epoch;
            _controller.MarkDiscontinuity();
        }

        _items = 0;
        _workMilliseconds = 0.0;
        _deferred = false;
    }

    private TextureStreamingWorkControllerOptions ResolveOptions()
    {
        RuntimeEngine.Rendering.EngineSettings settings = RuntimeEngine.Rendering.Settings;
        return new TextureStreamingWorkControllerOptions(
            _initialItemsPerFrame,
            TextureStreamingCapacity.MaxItemsPerFrame,
            settings.TextureStreamingFrameToleranceMilliseconds,
            settings.TextureStreamingUploadsPerFrame,
            settings.TextureStreamingMaxUploadsPerFrame);
    }

    private static double ResolveHeadroomShare()
        => TextureStreamingCapacity.IsCutsceneWaitingForResidency
            ? TextureStreamingCapacity.CutsceneHeadroomShare
            : TextureStreamingCapacity.DefaultHeadroomShare;
}
