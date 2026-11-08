namespace XREngine.Rendering;

/// <summary>
/// Feedback controller for the number of texture streaming items (uploads or publications) that one render frame does.
/// It measures the cost of each item and the frame time, raises the limit slowly while the frame has headroom, and lowers
/// it at once when a frame goes over the frame-time rule. The controller is deterministic: it uses only the samples that
/// the caller gives, so tests can drive it with fake clocks and fake costs. It allocates nothing.
/// </summary>
/// <remarks>
/// <para>The frame-time rule: streaming work must not make a frame longer than the normal frame budget plus
/// <see cref="TextureStreamingWorkControllerOptions.ToleranceMilliseconds"/>. The normal frame budget is the explicit
/// target frame period or the learned normal frame time, whichever is longer. The learned normal frame time is the mean
/// frame time without streaming plus <see cref="JitterDeviationFactor"/> times its mean deviation, so a machine that cannot
/// reach the target, or a scene with frame-time jitter, is not blamed on streaming.</para>
/// <para>Model target: <c>(tolerance + share * headroom) / costPerItem</c> items per frame. The cost per item is a peak of
/// the measured costs that decays toward their mean, so one slow item lowers the target at once and the target rises
/// again only slowly.</para>
/// <para>Reaction: an over-budget frame with lane work lowers the limit when the measured lane work explains at least
/// <see cref="OverrunAttributionShare"/> of the excess. Otherwise it lowers the limit only when the cost grows with the item
/// count that the CPU timer does not see (for example, driver work at the next present): frames with lane work go over the
/// budget more often than frames without it, and frames with two or more items go over more often for each item than
/// frames with one item (each by <see cref="OverrunCorrelationMargin"/>). Then the excess divided by the items is also a
/// cost sample. A frame that went over with <c>N</c> items, and that the measured work explains, sets a per-frame ceiling of
/// <c>N - 1</c> items for <see cref="CeilingLifetimeMilliseconds"/>; a statistical back-off sets no ceiling, so one false
/// back-off costs only the hold time. When frames with more items go over less often for each item than frames
/// with one item, the cost is mostly a fixed cost of each frame with lane work; fewer items in a frame would only add frames
/// that pay it, so the limit does not fall.</para>
/// <para>Floor: the limit does not go below one item per frame until the measured work explains an overrun. After that,
/// the floor is <c>tolerance / costPerItem</c> (at least <see cref="MinimumItemsPerFrame"/>), so an item that is too large
/// for one frame runs only as often as the tolerance pays for it on average.</para>
/// <para>Hysteresis: the limit rises toward the target only while work was deferred and the hold time of the last
/// back-off has passed, by at most <see cref="RiseFraction"/> in one frame, and never above the target. It falls without
/// an overrun only when the target is more than <see cref="Hysteresis"/> below it. A small drop of the target therefore
/// does not move the limit, and a steady cost and a steady frame time give a constant limit.</para>
/// </remarks>
internal sealed class TextureStreamingWorkController
{
    /// <summary>The multiplier of the limit after a back-off (multiplicative decrease).</summary>
    internal const double DecreaseFactor = 0.5;

    /// <summary>The time after a back-off before the limit can rise again. It is much longer than the GPU queue latency.</summary>
    internal const double HoldMilliseconds = 500.0;

    /// <summary>The time without an explained overrun before the floor goes back to one item per frame.</summary>
    internal const double RecoverMilliseconds = 2000.0;

    /// <summary>
    /// The time that the per-frame ceiling of an overrun stays active. After it, the controller can try the larger item count
    /// again, so a change of the scene or the machine load is learned, but at most one such probe occurs in this time.
    /// </summary>
    internal const double CeilingLifetimeMilliseconds = 10000.0;

    /// <summary>
    /// The decay of the peak item cost for each cost sample. The peak falls by half its distance to the mean cost in about
    /// 34 samples (about 0.6 s at 60 Hz) and never falls below the mean cost.
    /// </summary>
    internal const double CostPeakDecay = 0.98;

    /// <summary>The smoothing factor of the mean item cost.</summary>
    internal const double CostMeanSmoothing = 0.1;

    /// <summary>The smoothing factor of the idle render-thread time.</summary>
    internal const double IdleSmoothing = 0.1;

    /// <summary>
    /// The smoothing factor of the normal frame time and its deviation for frames without lane work. A new steady frame time
    /// is learned in about 60 such frames.
    /// </summary>
    internal const double NormalFrameSmoothing = 0.05;

    /// <summary>
    /// The smoothing factor of the normal frame time for frames with lane work, which count without the measured work.
    /// It is small, because work that the timer does not see stays in these frames; it only follows a slow scene change
    /// during a long streaming burst.
    /// </summary>
    internal const double NormalFrameWorkSmoothing = 0.01;

    /// <summary>
    /// The normal frame budget is the mean frame time plus this many mean deviations. For normal jitter, 2.5 mean deviations
    /// are about 2 standard deviations, so about 2 percent of the frames without streaming are above the budget.
    /// </summary>
    internal const double JitterDeviationFactor = 2.5;

    /// <summary>The smoothing factor of the over-budget rates of frames with and without lane work.</summary>
    internal const double OverrunRateSmoothing = 0.1;

    /// <summary>
    /// Frames with lane work must go over the budget more often than frames without it by this share before an over-budget
    /// frame that the measured work does not explain lowers the limit. With <see cref="OverrunRateSmoothing"/> = 0.1, two
    /// over-budget work frames in a row are enough when frames without work do not go over.
    /// </summary>
    internal const double OverrunCorrelationMargin = 0.1;

    /// <summary>
    /// The dead band below the current limit, as a fraction of it. The limit falls without an overrun only when the target
    /// is lower than this band. It is wider than the usual cost jitter of one upload path, so the jitter does not move the
    /// limit; an overrun lowers the limit at once in any case.
    /// </summary>
    internal const double Hysteresis = 0.15;

    /// <summary>The largest rise of the limit in one frame, as a fraction of the current limit.</summary>
    internal const double RiseFraction = 0.25;

    /// <summary>The smallest rise of the limit in one frame, in items per frame.</summary>
    internal const double MinimumRiseStep = 1.0 / 16.0;

    /// <summary>The lowest limit, in items per frame. Streaming never stops completely.</summary>
    internal const double MinimumItemsPerFrame = 1.0 / 32.0;

    /// <summary>
    /// The idle render-thread time must be below this share of the budget before the controller gives credit for headroom.
    /// A busy time near the budget means that the frame is full or that the busy time includes a present wait.
    /// </summary>
    internal const double IdleBusyHeadroomThreshold = 0.9;

    /// <summary>
    /// An over-budget frame is explained by this lane when the measured lane work is at least this share of the time above
    /// the budget.
    /// </summary>
    internal const double OverrunAttributionShare = 0.5;

    private TextureStreamingWorkControllerOptions _options;
    private double _rate;
    private double _credit;
    private double _costEstimate;
    private double _costMean;
    private double _normalFrameMean = double.NaN;
    private double _normalFrameDeviation;
    private double _idleBusyMilliseconds = double.NaN;
    private double _holdRemainingMilliseconds;
    private double _millisecondsSinceAttributedOverrun = double.PositiveInfinity;
    private int _ceilingItems;
    private double _ceilingAgeMilliseconds;
    private int _allowance;
    private long _overrunCount;
    private long _attributedOverrunCount;
    private long _backoffCount;
    private double _overRateWithWork;
    private double _overRateIdle;
    private double _overRateSingleItem;
    private double _overRateMultipleItems;
    private double _multipleItemsMean = 2.0;
    private double _lastHeadroomMilliseconds;
    private double _lastTargetItemsPerFrame;

    /// <summary>Creates a controller that starts at <see cref="TextureStreamingWorkControllerOptions.InitialItemsPerFrame"/>.</summary>
    public TextureStreamingWorkController(in TextureStreamingWorkControllerOptions options)
    {
        _options = options;
        _rate = Math.Clamp(options.InitialItemsPerFrame, 1, Math.Max(1, ResolveMaxRate()));
        _lastTargetItemsPerFrame = _rate;
        _allowance = ResolveAllowance();
    }

    /// <summary>The configuration. Change it with <see cref="Configure"/>.</summary>
    public TextureStreamingWorkControllerOptions Options => _options;

    /// <summary>The number of items that the current frame can do.</summary>
    public int ItemsThisFrame => _allowance;

    /// <summary>The adaptive limit in items per frame. A value below 1 means one item every few frames.</summary>
    public double ItemsPerFrame => _rate;

    /// <summary>The decaying peak cost of one item in milliseconds. Zero before the first measurement.</summary>
    public double CostPerItemMilliseconds => _costEstimate;

    /// <summary>
    /// The learned normal frame budget in milliseconds (mean plus jitter allowance), without an explicit target.
    /// <see cref="double.NaN"/> before the first frame measurement.
    /// </summary>
    public double BudgetMilliseconds => ResolveLearnedBudget();

    /// <summary>The idle headroom that the last update used, in milliseconds.</summary>
    public double HeadroomMilliseconds => _lastHeadroomMilliseconds;

    /// <summary>The model target of the last update, in items per frame.</summary>
    public double TargetItemsPerFrame => _lastTargetItemsPerFrame;

    /// <summary>The per-frame ceiling of the last back-off with more than one item. Zero when no ceiling is active.</summary>
    public int CeilingItems => _ceilingItems;

    /// <summary>The number of over-budget frames in which this lane did work.</summary>
    public long OverrunCount => _overrunCount;

    /// <summary>The number of over-budget frames that the measured lane work explains.</summary>
    public long AttributedOverrunCount => _attributedOverrunCount;

    /// <summary>The number of over-budget frames that lowered the limit.</summary>
    public long BackoffCount => _backoffCount;

    /// <summary>
    /// Changes the configuration. The adaptive limit stays where it is, inside the new bounds.
    /// </summary>
    public void Configure(in TextureStreamingWorkControllerOptions options)
    {
        if (_options == options)
            return;

        _options = options;
        _rate = Math.Min(_rate, ResolveMaxRate());
        _allowance = ResolveAllowance();
    }

    /// <summary>
    /// Adds a cost measurement that does not come from a frame sample, for example a GPU timestamp of a transfer that
    /// resolves some frames later.
    /// </summary>
    /// <param name="milliseconds">The cost of one item in milliseconds.</param>
    public void ObserveItemCost(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0.0)
            return;

        if (_costEstimate <= 0.0)
        {
            _costMean = milliseconds;
            _costEstimate = milliseconds;
            return;
        }

        // The estimate jumps up with a slow item at once and decays toward the mean, never below it. So cost jitter
        // around a steady mean moves the estimate only between the mean and the recent peak.
        _costMean += (milliseconds - _costMean) * CostMeanSmoothing;
        _costEstimate = Math.Max(milliseconds, Math.Max(_costMean, _costEstimate * CostPeakDecay));
    }

    /// <summary>
    /// Adds the average frame time of frames in which the lane was not active, for example the frames between two
    /// streaming bursts.
    /// </summary>
    public void ObserveIdleFrame(double frameMilliseconds, double busyMilliseconds)
    {
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds <= 0.0)
            return;

        ObserveNormalFrame(frameMilliseconds, 0.0, NormalFrameSmoothing, learnJitter: true, Math.Max(0.0, _options.ToleranceMilliseconds));
        ObserveIdleBusy(busyMilliseconds);
    }

    /// <summary>
    /// Adds the measurements of one completed frame and computes the limit of the next frame.
    /// </summary>
    /// <returns>The number of items that the next frame can do.</returns>
    public int Update(in TextureStreamingWorkSample sample)
    {
        double frameMilliseconds = sample.FrameMilliseconds;
        bool frameKnown = double.IsFinite(frameMilliseconds) && frameMilliseconds > 0.0;
        int items = Math.Max(0, sample.ItemsCompleted);
        double work = double.IsFinite(sample.WorkMilliseconds) && sample.WorkMilliseconds > 0.0 ? sample.WorkMilliseconds : 0.0;
        double tolerance = Math.Max(0.0, _options.ToleranceMilliseconds);
        bool idleFrame = items == 0 && work <= 0.0;

        _credit = Math.Max(0.0, _credit - items);
        if (items > 0)
            ObserveItemCost(work / items);

        // The budget of this frame is the one learned before it, so an overrun cannot raise its own limit.
        double budget = ResolveBudget(sample.TargetFrameMilliseconds);
        bool frameOver = frameKnown && double.IsFinite(budget) && frameMilliseconds > budget + tolerance;
        bool overBudget = frameOver && items > 0;
        bool attributed = overBudget && work >= OverrunAttributionShare * (frameMilliseconds - budget);
        bool growsWithItems = false;
        if (frameKnown && double.IsFinite(budget))
        {
            double over = frameOver ? 1.0 : 0.0;
            if (idleFrame)
                _overRateIdle += (over - _overRateIdle) * OverrunRateSmoothing;
            else
                _overRateWithWork += (over - _overRateWithWork) * OverrunRateSmoothing;

            if (items == 1)
                _overRateSingleItem += (over - _overRateSingleItem) * OverrunRateSmoothing;
            else if (items > 1)
            {
                _overRateMultipleItems += (over - _overRateMultipleItems) * OverrunRateSmoothing;
                _multipleItemsMean += (items - _multipleItemsMean) * OverrunRateSmoothing;
            }

            growsWithItems = items > 1
                && _overRateWithWork > _overRateIdle + OverrunCorrelationMargin
                && MultipleItemFramesOverrunMorePerItem(OverrunCorrelationMargin);
        }

        bool backoff = overBudget && (attributed || growsWithItems);

        if (frameKnown)
        {
            // The normal frame time is the frame without the measured lane work. Only frames without lane work teach the
            // jitter, because a frame limiter can hide the work of a work frame in its wait.
            ObserveNormalFrame(
                Math.Max(0.0, frameMilliseconds - work),
                sample.TargetFrameMilliseconds,
                idleFrame ? NormalFrameSmoothing : NormalFrameWorkSmoothing,
                idleFrame,
                tolerance);
            if (idleFrame)
                ObserveIdleBusy(sample.BusyMilliseconds);
        }

        budget = ResolveBudget(sample.TargetFrameMilliseconds);
        double elapsed = frameKnown ? frameMilliseconds : double.IsFinite(budget) ? budget : 0.0;
        AdvanceTimers(elapsed);

        double headroom = ResolveHeadroom(budget);
        _lastHeadroomMilliseconds = headroom;
        double share = double.IsFinite(sample.HeadroomShare) ? Math.Clamp(sample.HeadroomShare, 0.0, 1.0) : 0.0;
        double allowedCost = tolerance + share * headroom;
        double maxRate = ResolveMaxRate();
        double modelRate = _costEstimate > 0.0 ? allowedCost / _costEstimate : double.NaN;

        if (overBudget)
            _overrunCount++;

        if (backoff)
        {
            _backoffCount++;
            _holdRemainingMilliseconds = HoldMilliseconds;

            // The time above the budget is an upper bound of the item cost that the CPU timer can miss.
            ObserveItemCost((frameMilliseconds - budget) / items);
            modelRate = allowedCost / _costEstimate;

            if (attributed)
            {
                _attributedOverrunCount++;
                _millisecondsSinceAttributedOverrun = 0.0;
                if (items > 1)
                {
                    _ceilingItems = _ceilingItems > 0 ? Math.Min(_ceilingItems, items - 1) : items - 1;
                    _ceilingAgeMilliseconds = 0.0;
                }
            }

            double floor = ResolveFloor(maxRate);
            double reduced = Math.Min(_rate, items) * DecreaseFactor;
            reduced = Math.Min(reduced, Math.Max(modelRate, floor));
            _rate = Math.Clamp(reduced, floor, maxRate);
            _lastTargetItemsPerFrame = Math.Clamp(modelRate, floor, maxRate);
        }
        else
        {
            double floor = ResolveFloor(maxRate);
            double target = double.IsFinite(modelRate) ? Math.Max(modelRate, floor) : Math.Max(_rate, floor);
            target = Math.Min(target, maxRate);
            _lastTargetItemsPerFrame = target;

            if (target < _rate * (1.0 - Hysteresis))
            {
                _rate = target;
            }
            // An over-budget frame with several items does not raise the limit while frames with several items go over
            // more often than frames with one item, so the next frame can show whether the cost grows with the items.
            else if (sample.WorkDeferred
                && !(overBudget && items > 1 && MultipleItemFramesOverrunMorePerItem(0.0))
                && _holdRemainingMilliseconds <= 0.0
                && target > _rate)
            {
                _rate = Math.Min(target, _rate + Math.Max(MinimumRiseStep, _rate * RiseFraction));
            }
        }

        if (_ceilingItems > 0 && _ceilingAgeMilliseconds >= CeilingLifetimeMilliseconds)
            _ceilingItems = 0;

        // The rate stays inside the ceiling too, so the probe after the ceiling ends starts from the ceiling and rises
        // slowly instead of jumping to a rate that grew while the ceiling held the allowance.
        if (_ceilingItems > 0)
            _rate = Math.Min(_rate, _ceilingItems);

        _allowance = ResolveAllowance();
        return _allowance;
    }

    /// <summary>
    /// Gets whether frames with several items go over the budget more often than frames with one item would if each item
    /// paid the single-item overrun rate. A fixed cost of each work frame fails this test, because it does not grow with the
    /// items.
    /// </summary>
    private bool MultipleItemFramesOverrunMorePerItem(double margin)
        => _overRateMultipleItems > _overRateSingleItem * _multipleItemsMean + margin;

    private double ResolveLearnedBudget()
        => double.IsFinite(_normalFrameMean)
            ? _normalFrameMean + JitterDeviationFactor * _normalFrameDeviation
            : double.NaN;

    private double ResolveBudget(double targetFrameMilliseconds)
    {
        double learned = ResolveLearnedBudget();
        if (targetFrameMilliseconds <= 0.0)
            return learned;

        return double.IsFinite(learned) ? Math.Max(targetFrameMilliseconds, learned) : targetFrameMilliseconds;
    }

    private void ObserveNormalFrame(
        double frameMilliseconds,
        double targetFrameMilliseconds,
        double smoothing,
        bool learnJitter,
        double tolerance)
    {
        // A frame limiter holds a short frame to the target period, so the time below the target is not frame work.
        double counted = targetFrameMilliseconds > 0.0 ? Math.Max(frameMilliseconds, targetFrameMilliseconds) : frameMilliseconds;
        if (!double.IsFinite(_normalFrameMean))
        {
            _normalFrameMean = counted;
            _normalFrameDeviation = 0.0;
            return;
        }

        // A sample counts as at most the budget plus the tolerance, so a run of overruns raises the budget only a little
        // before the back-off acts.
        counted = Math.Min(counted, ResolveLearnedBudget() + tolerance);
        double deviation = Math.Abs(counted - _normalFrameMean);
        _normalFrameMean += (counted - _normalFrameMean) * smoothing;
        if (learnJitter)
            _normalFrameDeviation += (deviation - _normalFrameDeviation) * smoothing;
    }

    private void ObserveIdleBusy(double busyMilliseconds)
    {
        if (!double.IsFinite(busyMilliseconds) || busyMilliseconds < 0.0)
            return;

        _idleBusyMilliseconds = double.IsFinite(_idleBusyMilliseconds)
            ? _idleBusyMilliseconds + (busyMilliseconds - _idleBusyMilliseconds) * IdleSmoothing
            : busyMilliseconds;
    }

    private void AdvanceTimers(double elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= 0.0)
            return;

        _holdRemainingMilliseconds = Math.Max(0.0, _holdRemainingMilliseconds - elapsedMilliseconds);
        if (!double.IsPositiveInfinity(_millisecondsSinceAttributedOverrun))
            _millisecondsSinceAttributedOverrun += elapsedMilliseconds;
        if (_ceilingItems > 0)
            _ceilingAgeMilliseconds += elapsedMilliseconds;
    }

    private double ResolveHeadroom(double budget)
    {
        if (!double.IsFinite(budget) || budget <= 0.0 || !double.IsFinite(_idleBusyMilliseconds))
            return 0.0;

        // A busy time near the budget is a full frame or a present wait. Neither is headroom.
        if (_idleBusyMilliseconds >= budget * IdleBusyHeadroomThreshold)
            return 0.0;

        return budget - _idleBusyMilliseconds;
    }

    private double ResolveFloor(double maxRate)
    {
        double floor = 1.0;
        if (_millisecondsSinceAttributedOverrun < RecoverMilliseconds)
        {
            double tolerance = Math.Max(0.0, _options.ToleranceMilliseconds);
            floor = _costEstimate > 0.0
                ? Math.Clamp(tolerance / _costEstimate, MinimumItemsPerFrame, 1.0)
                : MinimumItemsPerFrame;
        }

        return Math.Min(floor, maxRate);
    }

    private double ResolveMaxRate()
    {
        int max = Math.Max(1, _options.MaxItemsPerFrame);
        if (_options.ItemsPerFrameCap is int cap)
            max = Math.Min(max, Math.Max(1, cap));
        return max;
    }

    private int ResolveAllowance()
    {
        if (_options.ItemsPerFrameOverride is int pinned)
            return Math.Max(1, pinned);

        int ceiling = _ceilingItems > 0 ? _ceilingItems : int.MaxValue;
        if (_rate >= 1.0)
        {
            _credit = 0.0;
            return Math.Min((int)Math.Floor(_rate), ceiling);
        }

        _credit = Math.Min(1.0, _credit + _rate);
        return _credit >= 1.0 ? 1 : 0;
    }
}
