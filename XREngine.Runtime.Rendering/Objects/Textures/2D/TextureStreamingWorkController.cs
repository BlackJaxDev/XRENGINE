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
/// frame time without the measured lane work plus <see cref="JitterDeviationFactor"/> times its mean deviation.</para>
/// <para>Scene changes: a camera cut or a teleport changes the scene cost, not the item cost. <see cref="MarkDiscontinuity"/>
/// (or a step in the frame time without more items, for <see cref="StepDetectFrames"/> frames) starts a short relearn of
/// <see cref="RelearnMilliseconds"/>. In it, the normal frame time follows the frames quickly and no statistical back-off
/// occurs. A scene excess is never a cost sample.</para>
/// <para>Item cost: a robust high value of the last <see cref="CostWindowSize"/> item costs (the value below the
/// <see cref="CostOutliersIgnored"/> highest), from the render-thread CPU time without GC pauses and from GPU timestamps.
/// One hitch in one item therefore does not move it. The effective work of a frame is the larger of the CPU work and the
/// item count times the mean GPU cost.</para>
/// <para>Model target: <c>(tolerance + share * headroom) / costPerItem</c> items per frame.</para>
/// <para>Reaction: an over-budget frame whose effective work explains at least <see cref="OverrunAttributionShare"/> of the
/// excess halves the limit and holds it for <see cref="HoldMilliseconds"/>. A second explained overrun in
/// <see cref="ExplainedOverrunRepeatMilliseconds"/> also sets a per-frame ceiling of <c>N - 1</c> items for
/// <see cref="CeilingLifetimeMilliseconds"/> and lets the floor go below one item per frame for
/// <see cref="RecoverMilliseconds"/>. An overrun that the work does not explain lowers the limit only when frames with
/// several items go over more often for each item than recent frames with one item (the baseline); it sets the
/// same ceiling, because it rests on several frames, but it gives no cost sample. A fixed cost of each work frame fails that test, so the limit does not fall for it.</para>
/// <para>Hysteresis: the limit rises toward the target only while work was deferred and the hold time has passed, by at
/// most <see cref="RiseFraction"/> in one frame, and never above the target. It falls without an overrun only when the
/// target is more than <see cref="Hysteresis"/> below it. Smoothing uses time constants, so machines with different frame
/// rates behave the same in time.</para>
/// </remarks>
internal sealed class TextureStreamingWorkController
{
    /// <summary>The multiplier of the limit after a back-off (multiplicative decrease).</summary>
    internal const double DecreaseFactor = 0.5;

    /// <summary>The time after a back-off before the limit can rise again. It is longer than the GPU queue latency.</summary>
    internal const double HoldMilliseconds = 250.0;

    /// <summary>The time after a repeated explained overrun in which the floor can be below one item per frame.</summary>
    internal const double RecoverMilliseconds = 1000.0;

    /// <summary>The time that the per-frame ceiling of a repeated explained overrun stays active.</summary>
    internal const double CeilingLifetimeMilliseconds = 2000.0;

    /// <summary>A second explained overrun in this time is a repeated overrun, not a single hitch.</summary>
    internal const double ExplainedOverrunRepeatMilliseconds = 1000.0;

    /// <summary>The number of recent item costs that the robust cost estimate uses.</summary>
    internal const int CostWindowSize = 16;

    /// <summary>The number of highest recent item costs that the estimate ignores as outliers (hitches).</summary>
    internal const int CostOutliersIgnored = 2;

    /// <summary>The smoothing factor of the mean GPU cost for each GPU sample.</summary>
    internal const double GpuCostSmoothing = 0.1;

    /// <summary>The time constant of the idle render-thread time.</summary>
    internal const double IdleBusyTimeConstantMilliseconds = 160.0;

    /// <summary>The time constant of the normal frame time and its deviation for frames without lane work.</summary>
    internal const double NormalFrameTimeConstantMilliseconds = 330.0;

    /// <summary>
    /// The time constant of the normal frame time for frames with lane work, which count without the effective work. It is
    /// long, because cost that no timer sees stays in these frames.
    /// </summary>
    internal const double NormalFrameWorkTimeConstantMilliseconds = 1660.0;

    /// <summary>
    /// The time constant of the normal frame time during a relearn, for the decode width controller. The work controller
    /// takes the lowest frame of the relearn instead, so a load stall in the relearn does not raise the budget.
    /// </summary>
    internal const double RelearnTimeConstantMilliseconds = 50.0;

    /// <summary>The duration of a relearn after a scene discontinuity.</summary>
    internal const double RelearnMilliseconds = 250.0;

    /// <summary>The fewest known frames in a relearn, so one long stall frame cannot end it alone.</summary>
    internal const int RelearnMinimumFrames = 4;

    /// <summary>
    /// The number of frames in a row whose time without the effective work is over the rule that count as a scene step,
    /// when the items do not explain them (no more items than a work frame inside the rule before the streak). It is shorter than the two frames that a
    /// statistical back-off needs, so a scene step starts its relearn first.
    /// </summary>
    internal const int StepDetectFrames = 2;


    /// <summary>
    /// The normal frame budget is the mean frame time plus this many mean deviations. For normal jitter, 2.5 mean deviations
    /// are about 2 standard deviations, so about 2 percent of the frames without streaming are above the budget.
    /// </summary>
    internal const double JitterDeviationFactor = 2.5;

    /// <summary>The time constant of the over-budget rates.</summary>
    internal const double OverrunRateTimeConstantMilliseconds = 160.0;

    /// <summary>A baseline over-budget rate is valid for a statistical back-off only from a frame in this recent time.</summary>
    internal const double BaselineRecencyMilliseconds = 500.0;

    /// <summary>
    /// Over-budget frames in a row with several items, and no recent baseline frame, after which the controller halves the
    /// limit once to get baseline frames (an exploratory back-off). It sets no ceiling and gives no cost sample, so a wrong
    /// probe costs one hold time. It is longer than <see cref="StepDetectFrames"/>, so a scene step starts its relearn first.
    /// </summary>
    internal const int ExploratoryBackoffFrames = 4;

    /// <summary>
    /// Frames with several items must go over the budget more often for each item than baseline frames by this share
    /// before an overrun that the work does not explain lowers the limit.
    /// </summary>
    internal const double OverrunCorrelationMargin = 0.1;

    /// <summary>The dead band below the current limit, as a fraction of it.</summary>
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

    /// <summary>An over-budget frame is explained by the lane when its effective work is at least this share of the excess.</summary>
    internal const double OverrunAttributionShare = 0.5;

    /// <summary>The frame time that the controller assumes when no frame time and no budget are known.</summary>
    private const double FallbackFrameMilliseconds = 1000.0 / 60.0;

    private readonly double[] _costWindow = new double[CostWindowSize];
    private readonly double[] _costScratch = new double[CostWindowSize];
    private int _costCount;
    private int _costNext;
    private TextureStreamingWorkControllerOptions _options;
    private double _rate;
    private double _credit;
    private double _costEstimate;
    private double _gpuCostMean;
    private double _normalFrameMean = double.NaN;
    private double _normalFrameDeviation;
    private double _idleBusyMilliseconds = double.NaN;
    private double _holdRemainingMilliseconds;
    private double _relearnRemainingMilliseconds = RelearnMilliseconds;
    private double _relearnMinimum = double.PositiveInfinity;
    private int _relearnFrames;
    private double _millisecondsSinceRepeatedOverrun = double.PositiveInfinity;
    private double _millisecondsSinceExplainedOverrun = double.PositiveInfinity;
    private double _millisecondsSinceBaselineFrame = double.PositiveInfinity;
    private double _millisecondsSinceExploratoryBackoff = double.PositiveInfinity;
    private int _ceilingItems;
    private double _ceilingAgeMilliseconds;
    private int _allowance;
    private int _stepFrames;
    private int _multipleItemOverStreak;
    private int _underFrames;
    private int _itemsBeforeStep;
    private int _previousItems;
    private bool _previousSceneOver;
    private long _overrunCount;
    private long _attributedOverrunCount;
    private long _backoffCount;
    private long _discontinuityCount;
    private double _overRateBaseline;
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

    /// <summary>The robust cost of one item in milliseconds. Zero before the first measurement.</summary>
    public double CostPerItemMilliseconds => _costEstimate;

    /// <summary>The mean GPU cost of one item in milliseconds. Zero before the first GPU measurement.</summary>
    public double GpuCostPerItemMilliseconds => _gpuCostMean;

    /// <summary>
    /// The learned normal frame budget in milliseconds (mean plus jitter allowance), without an explicit target.
    /// <see cref="double.NaN"/> before the first frame measurement.
    /// </summary>
    public double BudgetMilliseconds => ResolveLearnedBudget();

    /// <summary>The idle headroom that the last update used, in milliseconds.</summary>
    public double HeadroomMilliseconds => _lastHeadroomMilliseconds;

    /// <summary>The model target of the last update, in items per frame.</summary>
    public double TargetItemsPerFrame => _lastTargetItemsPerFrame;

    /// <summary>The per-frame ceiling of the last repeated or statistical overrun. Zero when no ceiling is active.</summary>
    public int CeilingItems => _ceilingItems;

    /// <summary>True while the controller relearns the normal frame time after a scene discontinuity.</summary>
    public bool IsRelearning => _relearnRemainingMilliseconds > 0.0;

    /// <summary>The number of over-budget frames in which this lane did work.</summary>
    public long OverrunCount => _overrunCount;

    /// <summary>The number of over-budget frames that the effective lane work explains.</summary>
    public long AttributedOverrunCount => _attributedOverrunCount;

    /// <summary>The number of over-budget frames that lowered the limit.</summary>
    public long BackoffCount => _backoffCount;

    /// <summary>The number of scene discontinuities (signaled or detected) that started a relearn.</summary>
    public long DiscontinuityCount => _discontinuityCount;

    /// <summary>Changes the configuration. The adaptive limit stays where it is, inside the new bounds.</summary>
    public void Configure(in TextureStreamingWorkControllerOptions options)
    {
        if (_options == options)
            return;

        _options = options;
        _rate = Math.Min(_rate, ResolveMaxRate());
        _allowance = ResolveAllowance();
    }

    /// <summary>
    /// Starts a relearn of the normal frame time, for example after a camera cut. The over-budget statistics restart, so
    /// the cost of the new scene does not count as item cost.
    /// </summary>
    public void MarkDiscontinuity()
    {
        _discontinuityCount++;
        _relearnRemainingMilliseconds = RelearnMilliseconds;
        _relearnMinimum = double.PositiveInfinity;
        _relearnFrames = 0;
        _overRateBaseline = 0.0;
        _overRateMultipleItems = 0.0;
        _millisecondsSinceBaselineFrame = double.PositiveInfinity;
        _stepFrames = 0;
        _multipleItemOverStreak = 0;
    }

    /// <summary>Adds a render-thread CPU cost measurement of one item.</summary>
    /// <param name="milliseconds">The cost of one item in milliseconds.</param>
    public void ObserveItemCost(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0.0)
            return;

        _costWindow[_costNext] = milliseconds;
        _costNext = (_costNext + 1) % CostWindowSize;
        if (_costCount < CostWindowSize)
            _costCount++;

        _costEstimate = ResolveRobustCost();
    }

    /// <summary>
    /// Adds a GPU timestamp measurement of one item, which can resolve some frames after the item. It updates the mean GPU
    /// cost that the effective work of later frames uses, and it is a cost sample too.
    /// </summary>
    public void ObserveGpuItemCost(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0.0)
            return;

        _gpuCostMean = _gpuCostMean <= 0.0
            ? milliseconds
            : _gpuCostMean + (milliseconds - _gpuCostMean) * GpuCostSmoothing;
        ObserveItemCost(milliseconds);
    }

    /// <summary>
    /// Adds the average frame time of frames in which the lane was not active, for example the frames between two
    /// streaming bursts.
    /// </summary>
    public void ObserveIdleFrame(double frameMilliseconds, double busyMilliseconds)
    {
        // A gap average can hold a load stall, so it never seeds the budget and always counts as at most the budget plus
        // the tolerance, also during a relearn.
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds <= 0.0 || !double.IsFinite(_normalFrameMean))
            return;

        double tolerance = Math.Max(0.0, _options.ToleranceMilliseconds);
        double counted = Math.Min(frameMilliseconds, ResolveLearnedBudget() + tolerance);
        double smoothing = Smoothing(NormalFrameTimeConstantMilliseconds, counted);
        double deviation = Math.Abs(counted - _normalFrameMean);
        _normalFrameMean += (counted - _normalFrameMean) * smoothing;
        _normalFrameDeviation += (deviation - _normalFrameDeviation) * smoothing;
        ObserveIdleBusy(busyMilliseconds, counted);
    }

    /// <summary>Adds the measurements of one completed frame and computes the limit of the next frame.</summary>
    /// <returns>The number of items that the next frame can do.</returns>
    public int Update(in TextureStreamingWorkSample sample)
    {
        double frameMilliseconds = sample.FrameMilliseconds;
        bool frameKnown = double.IsFinite(frameMilliseconds) && frameMilliseconds > 0.0;
        int items = Math.Max(0, sample.ItemsCompleted);
        double cpuWork = double.IsFinite(sample.WorkMilliseconds) && sample.WorkMilliseconds > 0.0 ? sample.WorkMilliseconds : 0.0;
        double work = Math.Max(cpuWork, items * _gpuCostMean);
        double tolerance = Math.Max(0.0, _options.ToleranceMilliseconds);
        bool idleFrame = items == 0 && cpuWork <= 0.0;

        _credit = Math.Max(0.0, _credit - items);
        if (items > 0 && cpuWork > 0.0)
            ObserveItemCost(cpuWork / items);

        if (!double.IsFinite(_normalFrameMean) && sample.TargetFrameMilliseconds > 0.0)
            _normalFrameMean = sample.TargetFrameMilliseconds;

        // The budget of this frame is the one learned before it, so an overrun cannot raise its own limit.
        double budget = ResolveBudget(sample.TargetFrameMilliseconds);
        double elapsed = frameKnown ? frameMilliseconds : double.IsFinite(budget) ? budget : FallbackFrameMilliseconds;
        bool relearning = _relearnRemainingMilliseconds > 0.0;
        bool frameOver = frameKnown && double.IsFinite(budget) && frameMilliseconds > budget + tolerance;
        bool overBudget = frameOver && items > 0;
        bool explained = overBudget && work >= OverrunAttributionShare * (frameMilliseconds - budget);

        // A scene step: the frame without the effective work is over the rule for several frames, and the items do not
        // explain it. After a work frame inside the rule, more items would explain it, so the item count must not grow.
        // After a frame without work, new work and a scene change can start together, so the excess must be much larger
        // than the work. Such an excess is scene cost.
        bool sceneOver = frameKnown && double.IsFinite(budget) && frameMilliseconds - work > budget + tolerance;
        if (sceneOver && !relearning)
        {
            // The reference is the last frame before the streak; it must itself be inside the rule.
            if (_stepFrames == 0)
                _itemsBeforeStep = _previousSceneOver ? 0 : _previousItems;
            // After a frame without work, new work and a scene change start together and cannot be told apart: a cut
            // there comes with the signal (MarkDiscontinuity), and the exploratory back-off tests new work.
            bool sceneLike = _itemsBeforeStep > 0 && items <= _itemsBeforeStep;
            _stepFrames = sceneLike ? _stepFrames + 1 : 0;
            if (_stepFrames >= StepDetectFrames)
            {
                MarkDiscontinuity();
                relearning = true;
            }
        }
        else if (!sceneOver)
        {
            _stepFrames = 0;
        }

        // A step down: the frame without the work is below the mean by more than the jitter allowance and two
        // tolerances for several frames (the scene became cheaper, or a slow period of another process ended).
        double floorFrame = sample.TargetFrameMilliseconds > 0.0 ? sample.TargetFrameMilliseconds : 0.0;
        bool sceneUnder = frameKnown && !relearning && double.IsFinite(_normalFrameMean)
            && Math.Max(frameMilliseconds - work, floorFrame) + 2.0 * tolerance + JitterDeviationFactor * _normalFrameDeviation < _normalFrameMean;
        _underFrames = sceneUnder ? _underFrames + 1 : 0;
        if (_underFrames >= StepDetectFrames)
        {
            MarkDiscontinuity();
            relearning = true;
            _underFrames = 0;
        }

        bool growsWithItems = false;
        bool exploratory = false;
        if (frameKnown && double.IsFinite(budget) && !relearning)
        {
            double rateAlpha = Smoothing(OverrunRateTimeConstantMilliseconds, elapsed);
            double over = frameOver ? 1.0 : 0.0;
            // The baseline is frames with exactly one item: a frame without work does not pay a fixed cost of each work
            // frame, so it would make that cost look like a cost of each item.
            if (items == 1)
            {
                _overRateBaseline += (over - _overRateBaseline) * rateAlpha;
                _millisecondsSinceBaselineFrame = 0.0;
            }
            else if (items > 1)
            {
                _overRateMultipleItems += (over - _overRateMultipleItems) * rateAlpha;
                _multipleItemsMean += (items - _multipleItemsMean) * rateAlpha;
            }

            _multipleItemOverStreak = frameOver && items > 1 ? _multipleItemOverStreak + 1 : 0;

            // At least StepDetectFrames over-budget frames in a row, so a scene step starts its relearn first.
            bool baselineRecent = _millisecondsSinceBaselineFrame <= BaselineRecencyMilliseconds;
            growsWithItems = items > 1
                && _multipleItemOverStreak >= StepDetectFrames
                && baselineRecent
                && MultipleItemFramesOverrunMorePerItem(OverrunCorrelationMargin);

            // Without baseline frames the per-item test cannot run, and cost that no timer sees would raise the limit
            // freely. Halve once to get frames with fewer items; the per-item test then decides.
            exploratory = items > 1
                && !baselineRecent
                && _multipleItemOverStreak >= ExploratoryBackoffFrames
                && _holdRemainingMilliseconds <= 0.0;
        }

        if (frameKnown)
        {
            double withoutWork = Math.Max(0.0, frameMilliseconds - work);
            if (relearning)
            {
                // The new normal frame time is the lowest frame of the relearn: a scene step moves every frame, and a
                // load stall moves only some of them.
                double counted = sample.TargetFrameMilliseconds > 0.0 ? Math.Max(withoutWork, sample.TargetFrameMilliseconds) : withoutWork;
                _relearnMinimum = Math.Min(_relearnMinimum, counted);
                _relearnFrames++;
            }
            else
            {
                // A work frame shorter than the normal frame time proves that the normal frame is shorter, so it teaches
                // as fast as a frame without work. Only a longer work frame can hold cost that no timer sees.
                bool teachesFast = idleFrame || (double.IsFinite(_normalFrameMean) && withoutWork < _normalFrameMean);
                double smoothing = Smoothing(teachesFast ? NormalFrameTimeConstantMilliseconds : NormalFrameWorkTimeConstantMilliseconds, elapsed);
                ObserveNormalFrame(withoutWork, sample.TargetFrameMilliseconds, smoothing, teachesFast, tolerance);
            }

            if (idleFrame)
                ObserveIdleBusy(sample.BusyMilliseconds, elapsed);
        }

        AdvanceTimers(elapsed);
        if (relearning && _relearnFrames < RelearnMinimumFrames)
            _relearnRemainingMilliseconds = Math.Max(_relearnRemainingMilliseconds, 1.0);
        if (relearning && _relearnRemainingMilliseconds <= 0.0 && double.IsFinite(_relearnMinimum))
        {
            // The jitter of the old scene does not belong to the new one; frames without work teach it again.
            _normalFrameMean = _relearnMinimum;
            _normalFrameDeviation = 0.0;
            _relearnMinimum = double.PositiveInfinity;
        }

        budget = ResolveBudget(sample.TargetFrameMilliseconds);

        double headroom = ResolveHeadroom(budget);
        _lastHeadroomMilliseconds = headroom;
        double share = double.IsFinite(sample.HeadroomShare) ? Math.Clamp(sample.HeadroomShare, 0.0, 1.0) : 0.0;
        double allowedCost = tolerance + share * headroom;
        double maxRate = ResolveMaxRate();
        double modelRate = _costEstimate > 0.0 ? allowedCost / _costEstimate : double.NaN;

        if (overBudget)
            _overrunCount++;

        bool backoff = explained || (overBudget && (growsWithItems || exploratory));
        if (backoff)
        {
            _backoffCount++;
            _holdRemainingMilliseconds = HoldMilliseconds;
            if (explained)
            {
                _attributedOverrunCount++;
                bool repeated = _millisecondsSinceExplainedOverrun <= ExplainedOverrunRepeatMilliseconds;
                _millisecondsSinceExplainedOverrun = 0.0;
                if (repeated)
                {
                    _millisecondsSinceRepeatedOverrun = 0.0;
                    SetCeiling(items);
                }
            }
            else if (growsWithItems)
            {
                // A statistical back-off already rests on several over-budget frames, so it is not a single hitch.
                SetCeiling(items);
            }
            else
            {
                // A second exploratory back-off in the ceiling lifetime shows that the item count, not a hitch, causes the
                // overruns, so it sets the ceiling too.
                if (_millisecondsSinceExploratoryBackoff <= CeilingLifetimeMilliseconds)
                    SetCeiling(items);
                _millisecondsSinceExploratoryBackoff = 0.0;
                _multipleItemOverStreak = 0;
            }

            double floor = ResolveFloor(maxRate);
            double reduced = Math.Min(_rate, items) * DecreaseFactor;
            if (double.IsFinite(modelRate))
                reduced = Math.Min(reduced, Math.Max(modelRate, floor));
            _rate = Math.Clamp(reduced, floor, maxRate);
            _lastTargetItemsPerFrame = double.IsFinite(modelRate) ? Math.Clamp(modelRate, floor, maxRate) : _rate;
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
            // An over-budget frame with several items does not raise the limit while such frames go over more often
            // for each item than baseline frames, so the next frame can show whether the cost grows with the items.
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

        // The rate stays inside the ceiling too, so the probe after the ceiling ends starts from the ceiling.
        if (_ceilingItems > 0)
            _rate = Math.Min(_rate, _ceilingItems);

        _previousItems = items;
        _previousSceneOver = sceneOver;
        _allowance = ResolveAllowance();
        return _allowance;
    }

    private void SetCeiling(int items)
    {
        if (items <= 1)
            return;

        _ceilingItems = _ceilingItems > 0 ? Math.Min(_ceilingItems, items - 1) : items - 1;
        _ceilingAgeMilliseconds = 0.0;
    }

    /// <summary>Converts a time constant to the smoothing factor of one step of <paramref name="elapsedMilliseconds"/>.</summary>
    private static double Smoothing(double timeConstantMilliseconds, double elapsedMilliseconds)
        => 1.0 - Math.Exp(-Math.Max(0.0, elapsedMilliseconds) / timeConstantMilliseconds);

    private double ResolveRobustCost()
    {
        int count = _costCount;
        Array.Copy(_costWindow, _costScratch, count);
        Array.Sort(_costScratch, 0, count);
        int ignored = Math.Min(CostOutliersIgnored, Math.Max(0, (count - 1) / 2));
        return _costScratch[count - 1 - ignored];
    }

    /// <summary>
    /// Gets whether frames with several items go over the budget more often than baseline frames would if each item paid
    /// the baseline overrun rate. A fixed cost of each work frame fails this test, because it does not grow with the items.
    /// </summary>
    private bool MultipleItemFramesOverrunMorePerItem(double margin)
        => _overRateMultipleItems > _overRateBaseline * _multipleItemsMean + margin;

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

        // Outside a relearn, a sample counts as at most the budget plus the tolerance, so a run of overruns raises the
        // budget only a little before the back-off acts.
        if (_relearnRemainingMilliseconds <= 0.0)
            counted = Math.Min(counted, ResolveLearnedBudget() + tolerance);
        double deviation = Math.Abs(counted - _normalFrameMean);
        _normalFrameMean += (counted - _normalFrameMean) * smoothing;
        if (learnJitter)
            _normalFrameDeviation += (deviation - _normalFrameDeviation) * smoothing;
    }

    private void ObserveIdleBusy(double busyMilliseconds, double elapsedMilliseconds)
    {
        if (!double.IsFinite(busyMilliseconds) || busyMilliseconds < 0.0)
            return;

        _idleBusyMilliseconds = double.IsFinite(_idleBusyMilliseconds)
            ? _idleBusyMilliseconds + (busyMilliseconds - _idleBusyMilliseconds) * Smoothing(IdleBusyTimeConstantMilliseconds, elapsedMilliseconds)
            : busyMilliseconds;
    }

    private void AdvanceTimers(double elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= 0.0)
            return;

        _holdRemainingMilliseconds = Math.Max(0.0, _holdRemainingMilliseconds - elapsedMilliseconds);
        _relearnRemainingMilliseconds = Math.Max(0.0, _relearnRemainingMilliseconds - elapsedMilliseconds);
        _millisecondsSinceRepeatedOverrun += elapsedMilliseconds;
        _millisecondsSinceExplainedOverrun += elapsedMilliseconds;
        _millisecondsSinceBaselineFrame += elapsedMilliseconds;
        _millisecondsSinceExploratoryBackoff += elapsedMilliseconds;
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
        if (_millisecondsSinceRepeatedOverrun < RecoverMilliseconds)
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
