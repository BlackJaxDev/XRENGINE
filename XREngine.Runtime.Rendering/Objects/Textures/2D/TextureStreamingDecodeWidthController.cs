namespace XREngine.Rendering;

/// <summary>
/// Frame-time feedback for the decode gate width. The decodes run on worker threads, so their frame cost is CPU contention
/// that no timer of the render thread sees. The controller compares the over-budget rate of frames with active decodes
/// against frames without decodes: it lowers the width only on that evidence, and it raises the width while decodes wait
/// and the frames with decodes are not worse. It is deterministic and allocates nothing.
/// </summary>
/// <remarks>
/// <para>Start and floor: the width starts at the topology base width and never goes below
/// <see cref="TextureStreamingCapacityModel.MinimumDecodeConcurrency"/> (the old fixed width) without evidence. With
/// evidence in the last <see cref="EvidenceMemoryMilliseconds"/>, the floor is 1.</para>
/// <para>Rise: while decodes wait and every permit is in use, the width grows by half (at least 1) each
/// <see cref="RiseIntervalMilliseconds"/>, up to the memory bound, so a fast machine reaches the bound in a few hundred
/// milliseconds.</para>
/// <para>Back-off: an over-budget frame with active decodes, when such frames go over more often than frames without
/// decodes by <see cref="EvidenceMargin"/>, halves the width and holds it for <see cref="HoldMilliseconds"/>. The width
/// that gave the evidence is a ceiling (less one) for <see cref="EvidenceMemoryMilliseconds"/>, so the rise does not bring
/// it back at once.</para>
/// <para>A scene discontinuity restarts the statistics and relearns the normal frame time for
/// <see cref="TextureStreamingWorkController.RelearnMilliseconds"/>.</para>
/// </remarks>
internal sealed class TextureStreamingDecodeWidthController
{
    /// <summary>The time between two rises of the width.</summary>
    internal const double RiseIntervalMilliseconds = 50.0;

    /// <summary>The time after a back-off before the width can rise again.</summary>
    internal const double HoldMilliseconds = 500.0;

    /// <summary>The time for which evidence lets the floor go below the old fixed width.</summary>
    internal const double EvidenceMemoryMilliseconds = 5000.0;

    /// <summary>The share by which frames with decodes must go over more often than frames without decodes.</summary>
    internal const double EvidenceMargin = 0.15;

    /// <summary>The number of frames with decodes, after a reset, before the statistics count as evidence.</summary>
    internal const int MinimumDecodingFrames = 8;

    /// <summary>The number of frames without decodes, after a reset, before the statistics count as evidence.</summary>
    internal const int MinimumIdleFrames = 8;

    private const double FallbackFrameMilliseconds = 1000.0 / 60.0;

    private int _baseWidth;
    private int _maxWidth;
    private int? _pinnedWidth;
    private double _width;
    private double _normalFrameMean = double.NaN;
    private double _normalFrameDeviation;
    private double _overRateDecoding;
    private double _overRateIdle;
    private int _decodingFrames;
    private int _idleFrames;
    private double _holdRemainingMilliseconds;
    private double _sinceRiseMilliseconds = double.PositiveInfinity;
    private double _sinceEvidenceMilliseconds = double.PositiveInfinity;
    private double _relearnRemainingMilliseconds = TextureStreamingWorkController.RelearnMilliseconds;
    private double _relearnMinimum = double.PositiveInfinity;
    private int _relearnFrames;
    private double _tolerance = 2.0;
    private long _backoffCount;
    private double _evidenceCeiling = double.PositiveInfinity;

    /// <summary>Creates a controller.</summary>
    public TextureStreamingDecodeWidthController(int baseWidth, int maxWidth, int? pinnedWidth, double toleranceMilliseconds)
    {
        Configure(baseWidth, maxWidth, pinnedWidth, toleranceMilliseconds);
        _width = Math.Clamp(baseWidth, 1, Math.Max(1, maxWidth));
    }

    /// <summary>The gate width for the next frames.</summary>
    public int Width => _pinnedWidth is int pinned ? pinned : (int)Math.Floor(_width);

    /// <summary>The base width from the execution topology.</summary>
    public int BaseWidth => _baseWidth;

    /// <summary>The number of back-offs on evidence.</summary>
    public long BackoffCount => _backoffCount;

    /// <summary>True when evidence in the last <see cref="EvidenceMemoryMilliseconds"/> lets the floor go to 1.</summary>
    public bool HasRecentEvidence => _sinceEvidenceMilliseconds <= EvidenceMemoryMilliseconds;

    /// <summary>Changes the bounds. A pinned width overrides the feedback.</summary>
    public void Configure(int baseWidth, int maxWidth, int? pinnedWidth, double toleranceMilliseconds)
    {
        _maxWidth = Math.Max(1, maxWidth);
        _baseWidth = Math.Clamp(baseWidth, 1, _maxWidth);
        _pinnedWidth = pinnedWidth is int pinned ? Math.Max(1, pinned) : null;
        _tolerance = Math.Max(0.0, toleranceMilliseconds);
        _width = Math.Min(_width, _maxWidth);
    }

    /// <summary>Restarts the statistics after a scene discontinuity.</summary>
    public void MarkDiscontinuity()
    {
        _overRateDecoding = 0.0;
        _overRateIdle = 0.0;
        _decodingFrames = 0;
        _idleFrames = 0;
        _relearnRemainingMilliseconds = TextureStreamingWorkController.RelearnMilliseconds;
        _relearnMinimum = double.PositiveInfinity;
        _relearnFrames = 0;
    }

    /// <summary>Adds one frame and returns the width for the next frames.</summary>
    public int Update(in TextureStreamingDecodeSample sample)
    {
        double frame = sample.FrameMilliseconds;
        bool known = double.IsFinite(frame) && frame > 0.0;
        if (!double.IsFinite(_normalFrameMean) && sample.TargetFrameMilliseconds > 0.0)
            _normalFrameMean = sample.TargetFrameMilliseconds;

        double budget = ResolveBudget(sample.TargetFrameMilliseconds);
        double elapsed = known ? frame : double.IsFinite(budget) ? budget : FallbackFrameMilliseconds;
        bool decoding = sample.ActiveDecodes > 0;
        bool relearning = _relearnRemainingMilliseconds > 0.0;
        bool over = known && double.IsFinite(budget) && frame > budget + _tolerance;
        bool evidence = false;

        if (known && !relearning && double.IsFinite(budget))
        {
            double alpha = 1.0 - Math.Exp(-elapsed / TextureStreamingWorkController.OverrunRateTimeConstantMilliseconds);
            if (decoding)
            {
                _overRateDecoding += ((over ? 1.0 : 0.0) - _overRateDecoding) * alpha;
                _decodingFrames++;
            }
            else
            {
                _overRateIdle += ((over ? 1.0 : 0.0) - _overRateIdle) * alpha;
                _idleFrames++;
            }

            evidence = _decodingFrames >= MinimumDecodingFrames
                && _idleFrames >= MinimumIdleFrames
                && _overRateDecoding > _overRateIdle + EvidenceMargin;
        }

        // Frames without decodes teach the normal frame time. During a relearn, the lowest frame becomes the new normal
        // frame time at its end, so a load stall does not raise it.
        if (known && relearning)
        {
            _relearnMinimum = Math.Min(_relearnMinimum, sample.TargetFrameMilliseconds > 0.0 ? Math.Max(frame, sample.TargetFrameMilliseconds) : frame);
            _relearnFrames++;
        }
        else if (known && !decoding)
        {
            ObserveNormalFrame(
                frame,
                sample.TargetFrameMilliseconds,
                1.0 - Math.Exp(-elapsed / TextureStreamingWorkController.NormalFrameTimeConstantMilliseconds),
                relearning: false);
        }

        _holdRemainingMilliseconds = Math.Max(0.0, _holdRemainingMilliseconds - elapsed);
        _relearnRemainingMilliseconds = Math.Max(0.0, _relearnRemainingMilliseconds - elapsed);
        if (relearning && _relearnFrames < TextureStreamingWorkController.RelearnMinimumFrames)
            _relearnRemainingMilliseconds = Math.Max(_relearnRemainingMilliseconds, 1.0);
        if (relearning && _relearnRemainingMilliseconds <= 0.0 && double.IsFinite(_relearnMinimum))
        {
            _normalFrameMean = _relearnMinimum;
            _relearnMinimum = double.PositiveInfinity;
        }
        _sinceRiseMilliseconds += elapsed;
        _sinceEvidenceMilliseconds += elapsed;

        if (evidence && over && decoding)
        {
            _backoffCount++;
            _sinceEvidenceMilliseconds = 0.0;
            _holdRemainingMilliseconds = HoldMilliseconds;
            double current = Math.Floor(_width);
            _evidenceCeiling = Math.Min(_evidenceCeiling, Math.Max(1.0, current - 1.0));
            _width = Math.Max(ResolveFloor(), Math.Floor(current * 0.5));
        }
        else if (sample.WaitingDecodes > 0
            && sample.ActiveDecodes >= Width
            && _holdRemainingMilliseconds <= 0.0
            && _sinceRiseMilliseconds >= RiseIntervalMilliseconds
            && !(_decodingFrames >= MinimumDecodingFrames && _overRateDecoding > _overRateIdle + EvidenceMargin * 0.5))
        {
            _sinceRiseMilliseconds = 0.0;
            _width = Math.Min(_maxWidth, Math.Floor(_width) + Math.Max(1.0, Math.Floor(_width * 0.5)));
        }

        if (!HasRecentEvidence)
            _evidenceCeiling = double.PositiveInfinity;

        double ceiling = Math.Min(_maxWidth, _evidenceCeiling);
        double floor = Math.Min(ResolveFloor(), ceiling);
        _width = Math.Clamp(_width, floor, ceiling);
        return Width;
    }

    private double ResolveFloor()
        => HasRecentEvidence ? 1.0 : Math.Min(_maxWidth, TextureStreamingCapacityModel.MinimumDecodeConcurrency);

    private double ResolveBudget(double target)
    {
        double learned = double.IsFinite(_normalFrameMean)
            ? _normalFrameMean + TextureStreamingWorkController.JitterDeviationFactor * _normalFrameDeviation
            : double.NaN;
        if (target <= 0.0)
            return learned;

        return double.IsFinite(learned) ? Math.Max(target, learned) : target;
    }

    private void ObserveNormalFrame(double frame, double target, double smoothing, bool relearning)
    {
        double counted = target > 0.0 ? Math.Max(frame, target) : frame;
        if (!double.IsFinite(_normalFrameMean))
        {
            _normalFrameMean = counted;
            return;
        }

        if (!relearning)
            counted = Math.Min(counted, ResolveBudget(0.0) + _tolerance);
        double deviation = Math.Abs(counted - _normalFrameMean);
        _normalFrameMean += (counted - _normalFrameMean) * smoothing;
        _normalFrameDeviation += (deviation - _normalFrameDeviation) * smoothing;
    }
}
