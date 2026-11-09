namespace XREngine.Rendering;

/// <summary>
/// Feedback controller for the number of texture streaming items (uploads or publications) that one render frame does.
/// It measures the cost of each item and the frame time, raises the limit slowly while the frame has headroom, and lowers
/// it at once when a frame with streaming work goes over the frame-time rule. The controller is deterministic: it uses
/// only the samples that the caller gives, so tests can drive it with fake clocks and fake costs. It allocates nothing.
/// </summary>
/// <remarks>
/// <para>The frame-time rule: a frame with streaming work must not be longer than the frame budget plus
/// <see cref="TextureStreamingWorkControllerOptions.ToleranceMilliseconds"/>.</para>
/// <para>The frame budget comes only from frames without streaming work (idle frames). It is the mean idle frame time
/// plus <see cref="JitterDeviationFactor"/> times its mean deviation, but at most the frame interval plus the tolerance.
/// The frame interval is the target frame period of the engine, or the mean idle frame time when the scene is slower than
/// the target or when the engine has no target (for example, with VSync, where the mean idle frame time is the present
/// interval). The budget is never below the target frame period. A frame with work never raises the budget.</para>
/// <para>Idle frames: an idle frame counts as at most the frame interval plus the tolerance, so one stall moves the mean
/// only a little. Two idle frames in a row above that limit are a scene step: the mean becomes the lower of the two. Two
/// frames in a row (with or without work) whose time without the measured work is far below the mean are a step down: the
/// mean becomes the higher of the two. A step down can only lower the mean.</para>
/// <para>Probes: a probe is <see cref="ProbeFrames"/> frames without work, and the mean becomes the lowest of them. A
/// probe starts at the first frame, after a scene discontinuity (<see cref="MarkDiscontinuity"/>), and after a frame with
/// one item that still goes over the rule. Probes start at most once in <see cref="ProbeIntervalMilliseconds"/>; a
/// discontinuity inside that time waits for its end, so a scrub that signals each frame stops streaming for at most
/// <see cref="ProbeFrames"/> frames in each interval.</para>
/// <para>Back-off: a frame with work that goes over the rule always counts against the work, also when no timer saw the
/// cost. It halves the limit and holds it for <see cref="HoldMilliseconds"/>. The frame time above the normal frame is a
/// cost sample of its items. A second back-off in <see cref="RepeatedBackoffMilliseconds"/> sets a per-frame ceiling of
/// <c>N - 1</c> items for <see cref="CeilingLifetimeMilliseconds"/> and lets the floor go below one item per frame for
/// <see cref="RecoverMilliseconds"/>.</para>
/// <para>Item cost: a robust high value of the last <see cref="CostWindowSize"/> item costs (the value below the
/// <see cref="CostOutliersIgnored"/> highest), from the render-thread CPU time without GC pauses, from GPU timestamps, and
/// from back-off frames. One hitch in one item therefore does not move it. The effective work of a frame is the larger of
/// the CPU work and the item count times the mean GPU cost.</para>
/// <para>Excess cost: many cheap timed samples push a back-off sample out of the robust cost quickly, so cost that no timer
/// sees would be forgotten. Each back-off therefore also keeps the smaller of its excess per item and the excess of the
/// previous back-off as the excess cost. Both values decay with <see cref="ExcessCostTimeConstantMilliseconds"/>, so back-offs
/// that are some seconds apart still count, but one back-off alone (a possible hitch) does not set it. The model uses the
/// larger of the robust cost and the excess cost. A probe or two idle
/// frames that show a slower scene restart it, because the excess before them was scene cost.</para>
/// <para>Model target: <c>(tolerance + share * headroom) / costPerItem</c> items per frame.</para>
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

    /// <summary>The time after a repeated back-off in which the floor can be below one item per frame.</summary>
    internal const double RecoverMilliseconds = 1000.0;

    /// <summary>The time that the per-frame ceiling of a repeated back-off stays active.</summary>
    internal const double CeilingLifetimeMilliseconds = 2000.0;

    /// <summary>A second back-off in this time is a repeated back-off, not a single hitch.</summary>
    internal const double RepeatedBackoffMilliseconds = 1000.0;

    /// <summary>The number of recent item costs that the robust cost estimate uses.</summary>
    internal const int CostWindowSize = 16;

    /// <summary>The number of highest recent item costs that the estimate ignores as outliers (hitches).</summary>
    internal const int CostOutliersIgnored = 2;

    /// <summary>The time constant of the decay of the excess cost that back-offs keep.</summary>
    internal const double ExcessCostTimeConstantMilliseconds = 2000.0;

    /// <summary>The smoothing factor of the mean GPU cost for each GPU sample.</summary>
    internal const double GpuCostSmoothing = 0.1;

    /// <summary>The time constant of the idle render-thread time.</summary>
    internal const double IdleBusyTimeConstantMilliseconds = 160.0;

    /// <summary>The time constant of the mean idle frame time and its deviation.</summary>
    internal const double NormalFrameTimeConstantMilliseconds = 330.0;

    /// <summary>The number of frames without work in one probe.</summary>
    internal const int ProbeFrames = 2;

    /// <summary>
    /// The most frames that one probe can take. A probe frame without a known frame time does not count, but the probe
    /// ends after this many frames, so streaming does not stop.
    /// </summary>
    internal const int ProbeMaximumFrames = 4;

    /// <summary>The shortest time between the starts of two probes.</summary>
    internal const double ProbeIntervalMilliseconds = 1000.0;

    /// <summary>The number of frames in a row, far below the mean, that are a step down of the scene cost.</summary>
    internal const int StepDownFrames = 2;

    /// <summary>
    /// The frame budget is the mean idle frame time plus this many mean deviations. For normal jitter, 2.5 mean deviations
    /// are about 2 standard deviations, so about 2 percent of the idle frames are above the budget.
    /// </summary>
    internal const double JitterDeviationFactor = 2.5;

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
    private double _excessCost;
    private double _lastBackoffExcess;
    private double _gpuCostMean;
    private double _idleMean = double.NaN;
    private double _idleDeviation;
    private double _idleBusyMilliseconds = double.NaN;
    private double _lastTargetFrameMilliseconds;
    private double _previousIdleAboveLimit = double.NaN;
    private double _previousUnder = double.NaN;
    private double _holdRemainingMilliseconds;
    private double _millisecondsSinceBackoff = double.PositiveInfinity;
    private double _millisecondsSinceRepeatedBackoff = double.PositiveInfinity;
    private double _millisecondsSinceProbe = double.PositiveInfinity;
    private bool _probePending = true;
    private int _probeFramesLeft;
    private int _probeSamples;
    private double _probeMinimum = double.PositiveInfinity;
    private int _ceilingItems;
    private double _ceilingAgeMilliseconds;
    private int _allowance;
    private int _underFrames;
    private long _overrunCount;
    private long _backoffCount;
    private long _discontinuityCount;
    private long _probeCount;
    private double _lastHeadroomMilliseconds;
    private double _lastTargetItemsPerFrame;

    /// <summary>
    /// Creates a controller that starts at <see cref="TextureStreamingWorkControllerOptions.InitialItemsPerFrame"/>. The
    /// first frames are a probe, so the budget comes from frames without work.
    /// </summary>
    public TextureStreamingWorkController(in TextureStreamingWorkControllerOptions options)
    {
        _options = options;
        _rate = Math.Clamp(options.InitialItemsPerFrame, 1, Math.Max(1, ResolveMaxRate()));
        _lastTargetItemsPerFrame = _rate;
        TryStartProbe();
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

    /// <summary>The excess cost of one item that back-offs keep, in milliseconds. It decays to zero.</summary>
    public double ExcessCostPerItemMilliseconds => _excessCost;

    /// <summary>The cost of one item that the model uses: the larger of the robust cost and the excess cost.</summary>
    public double ModelCostPerItemMilliseconds => Math.Max(_costEstimate, _excessCost);

    /// <summary>The mean GPU cost of one item in milliseconds. Zero before the first GPU measurement.</summary>
    public double GpuCostPerItemMilliseconds => _gpuCostMean;

    /// <summary>
    /// The frame budget in milliseconds for the target frame period of the last sample. <see cref="double.NaN"/> before the
    /// first frame measurement when the engine has no target.
    /// </summary>
    public double BudgetMilliseconds => ResolveBudget(_lastTargetFrameMilliseconds);

    /// <summary>The idle headroom that the last update used, in milliseconds.</summary>
    public double HeadroomMilliseconds => _lastHeadroomMilliseconds;

    /// <summary>The model target of the last update, in items per frame.</summary>
    public double TargetItemsPerFrame => _lastTargetItemsPerFrame;

    /// <summary>The per-frame ceiling of the last repeated back-off. Zero when no ceiling is active.</summary>
    public int CeilingItems => _ceilingItems;

    /// <summary>True while a probe holds the work back to measure frames without work.</summary>
    public bool IsProbing => _probeFramesLeft > 0;

    /// <summary>The number of over-budget frames in which this lane did work.</summary>
    public long OverrunCount => _overrunCount;

    /// <summary>The number of over-budget frames that lowered the limit.</summary>
    public long BackoffCount => _backoffCount;

    /// <summary>The number of signaled scene discontinuities.</summary>
    public long DiscontinuityCount => _discontinuityCount;

    /// <summary>The number of probes that started.</summary>
    public long ProbeCount => _probeCount;

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
    /// Signals a scene discontinuity, for example a camera cut. The scene cost changes, so the controller probes the new
    /// frame time without work. The mean GPU cost restarts, because the GPU load of the new scene can differ.
    /// </summary>
    public void MarkDiscontinuity()
    {
        _discontinuityCount++;
        _gpuCostMean = 0.0;
        _probePending = true;
        TryStartProbe();
        _allowance = ResolveAllowance();
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
    /// streaming bursts. An average can hold a load stall, so it never seeds the mean and never counts as a scene step.
    /// </summary>
    public void ObserveIdleFrame(double frameMilliseconds, double busyMilliseconds)
    {
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds <= 0.0 || !double.IsFinite(_idleMean))
            return;

        double counted = Math.Min(CountedFrame(frameMilliseconds, _lastTargetFrameMilliseconds), ResolveIdleLimit(_lastTargetFrameMilliseconds));
        LearnIdle(counted, counted);
        ObserveIdleBusy(busyMilliseconds, counted);
    }

    /// <summary>Adds the measurements of one completed frame and computes the limit of the next frame.</summary>
    /// <returns>The number of items that the next frame can do.</returns>
    public int Update(in TextureStreamingWorkSample sample)
    {
        double target = sample.TargetFrameMilliseconds > 0.0 ? sample.TargetFrameMilliseconds : 0.0;
        _lastTargetFrameMilliseconds = target;
        double frameMilliseconds = sample.FrameMilliseconds;
        bool frameKnown = double.IsFinite(frameMilliseconds) && frameMilliseconds > 0.0;
        int items = Math.Max(0, sample.ItemsCompleted);
        double cpuWork = double.IsFinite(sample.WorkMilliseconds) && sample.WorkMilliseconds > 0.0 ? sample.WorkMilliseconds : 0.0;
        double work = Math.Max(cpuWork, items * _gpuCostMean);
        double tolerance = Math.Max(0.0, _options.ToleranceMilliseconds);
        bool idleFrame = items == 0 && cpuWork <= 0.0;
        bool probeFrame = _probeFramesLeft > 0;

        _credit = Math.Max(0.0, _credit - items);

        // The budget of this frame is the one learned before it, so an overrun cannot raise its own limit.
        double budget = ResolveBudget(target);
        double elapsed = frameKnown ? frameMilliseconds : double.IsFinite(budget) ? budget : FallbackFrameMilliseconds;
        bool overBudget = frameKnown && items > 0 && double.IsFinite(budget) && frameMilliseconds > budget + tolerance;

        if (items > 0 && cpuWork > 0.0 && !overBudget)
            ObserveItemCost(cpuWork / items);

        if (frameKnown)
        {
            double counted = CountedFrame(frameMilliseconds, target);
            if (probeFrame)
            {
                if (idleFrame)
                {
                    _probeMinimum = Math.Min(_probeMinimum, counted);
                    _probeSamples++;
                }
            }
            else if (idleFrame)
            {
                ObserveIdleSample(counted, elapsed, target);
            }
            else
            {
                _previousIdleAboveLimit = double.NaN;
            }

            ObserveStepDown(CountedFrame(Math.Max(0.0, frameMilliseconds - work), target), tolerance);
            if (idleFrame)
                ObserveIdleBusy(sample.BusyMilliseconds, elapsed);
        }

        if (probeFrame)
            AdvanceProbe();

        AdvanceTimers(elapsed);
        if (_probePending)
            TryStartProbe();

        budget = ResolveBudget(target);
        double headroom = ResolveHeadroom(budget);
        _lastHeadroomMilliseconds = headroom;
        double share = double.IsFinite(sample.HeadroomShare) ? Math.Clamp(sample.HeadroomShare, 0.0, 1.0) : 0.0;
        double maxRate = ResolveMaxRate();

        if (overBudget)
        {
            _overrunCount++;
            _backoffCount++;
            _holdRemainingMilliseconds = HoldMilliseconds;

            // The frame time above the normal frame belongs to the items, whether or not a timer saw it.
            double normalFrame = double.IsFinite(_idleMean) ? Math.Max(target, _idleMean) : budget;
            double excess = Math.Max(work, frameMilliseconds - normalFrame) / items;
            ObserveItemCost(excess);

            bool repeated = _millisecondsSinceBackoff <= RepeatedBackoffMilliseconds;
            _millisecondsSinceBackoff = 0.0;
            if (repeated)
            {
                _millisecondsSinceRepeatedBackoff = 0.0;
                SetCeiling(items);
            }

            // The smaller of the two excess samples, so one hitch in either frame does not set a large excess cost.
            _excessCost = Math.Max(_excessCost, Math.Min(excess, _lastBackoffExcess));
            _lastBackoffExcess = Math.Max(_lastBackoffExcess, excess);

            // One item still goes over: measure the frame without work, to tell a scene step from the cost of the item.
            if (items <= 1)
            {
                _probePending = true;
                TryStartProbe();
            }
        }

        double allowedCost = tolerance + share * headroom;
        double modelCost = Math.Max(_costEstimate, _excessCost);
        double modelRate = modelCost > 0.0 ? allowedCost / modelCost : double.NaN;
        double floor = ResolveFloor(maxRate);
        if (overBudget)
        {
            double reduced = Math.Min(_rate, items) * DecreaseFactor;
            if (double.IsFinite(modelRate))
                reduced = Math.Min(reduced, Math.Max(modelRate, floor));
            _rate = Math.Clamp(reduced, floor, maxRate);
            _lastTargetItemsPerFrame = double.IsFinite(modelRate) ? Math.Clamp(modelRate, floor, maxRate) : _rate;
        }
        else
        {
            double rateTarget = double.IsFinite(modelRate) ? Math.Max(modelRate, floor) : Math.Max(_rate, floor);
            rateTarget = Math.Min(rateTarget, maxRate);
            _lastTargetItemsPerFrame = rateTarget;

            if (rateTarget < _rate * (1.0 - Hysteresis))
                _rate = rateTarget;
            else if (sample.WorkDeferred && _holdRemainingMilliseconds <= 0.0 && rateTarget > _rate)
                _rate = Math.Min(rateTarget, _rate + Math.Max(MinimumRiseStep, _rate * RiseFraction));
        }

        if (_ceilingItems > 0 && _ceilingAgeMilliseconds >= CeilingLifetimeMilliseconds)
            _ceilingItems = 0;

        // The rate stays inside the ceiling too, so the probe after the ceiling ends starts from the ceiling.
        if (_ceilingItems > 0)
            _rate = Math.Min(_rate, _ceilingItems);

        _allowance = ResolveAllowance();
        return _allowance;
    }

    /// <summary>
    /// Gets the frame budget from the mean and mean deviation of the idle frames: the mean plus
    /// <see cref="JitterDeviationFactor"/> deviations, at most the frame interval plus the tolerance, and at least the
    /// target. The frame interval is the larger of the target and the mean (the mean alone when there is no target).
    /// </summary>
    internal static double ResolveBoundedBudget(double idleMean, double idleDeviation, double targetFrameMilliseconds, double toleranceMilliseconds)
    {
        double target = targetFrameMilliseconds > 0.0 ? targetFrameMilliseconds : 0.0;
        if (!double.IsFinite(idleMean))
            return target > 0.0 ? target : double.NaN;

        double interval = Math.Max(target, idleMean);
        double learned = Math.Min(idleMean + JitterDeviationFactor * Math.Max(0.0, idleDeviation), interval + Math.Max(0.0, toleranceMilliseconds));
        return Math.Max(target, learned);
    }

    private double ResolveBudget(double target)
        => ResolveBoundedBudget(_idleMean, _idleDeviation, target, _options.ToleranceMilliseconds);

    /// <summary>The highest value that one idle frame counts as: the frame interval plus the tolerance.</summary>
    private double ResolveIdleLimit(double target)
        => Math.Max(target, _idleMean) + Math.Max(0.0, _options.ToleranceMilliseconds);

    /// <summary>A frame limiter holds a short frame to the target period, so the time below the target is not frame work.</summary>
    private static double CountedFrame(double frameMilliseconds, double target)
        => target > 0.0 ? Math.Max(frameMilliseconds, target) : frameMilliseconds;

    private void ObserveIdleSample(double counted, double elapsed, double target)
    {
        if (!double.IsFinite(_idleMean))
        {
            _idleMean = counted;
            _idleDeviation = 0.0;
            return;
        }

        double limit = ResolveIdleLimit(target);
        if (counted <= limit)
        {
            _previousIdleAboveLimit = double.NaN;
            LearnIdle(counted, Math.Min(elapsed, limit));
            return;
        }

        // Two idle frames in a row above the limit are a scene step, not a stall: the lower of them is the new mean.
        if (double.IsFinite(_previousIdleAboveLimit))
        {
            SetSceneMean(Math.Min(counted, _previousIdleAboveLimit), target);
            _previousIdleAboveLimit = double.NaN;
            return;
        }

        // One stall frame counts as the limit, with the weight of a frame of that length.
        _previousIdleAboveLimit = counted;
        LearnIdle(limit, limit);
    }

    /// <summary>
    /// Sets the mean from frames without work. When the new mean is above the idle limit, the scene became slower, so the
    /// back-off excess of the frames before it was scene cost, not item cost: the excess cost restarts.
    /// </summary>
    private void SetSceneMean(double mean, double target)
    {
        if (double.IsFinite(_idleMean) && mean > ResolveIdleLimit(target))
        {
            _excessCost = 0.0;
            _lastBackoffExcess = 0.0;
        }

        _idleMean = mean;
    }

    private void LearnIdle(double counted, double elapsed)
    {
        double smoothing = Smoothing(NormalFrameTimeConstantMilliseconds, elapsed);
        double deviation = Math.Abs(counted - _idleMean);
        _idleMean += (counted - _idleMean) * smoothing;
        _idleDeviation += (deviation - _idleDeviation) * smoothing;
    }

    private void ObserveStepDown(double withoutWork, double tolerance)
    {
        // A step down can only lower the mean, so it can use frames with work: their time without the measured work is at
        // least the scene cost.
        bool under = double.IsFinite(_idleMean)
            && withoutWork + 2.0 * tolerance + JitterDeviationFactor * _idleDeviation < _idleMean;
        if (!under)
        {
            _underFrames = 0;
            _previousUnder = double.NaN;
            return;
        }

        _underFrames++;
        _previousUnder = double.IsFinite(_previousUnder) ? Math.Max(_previousUnder, withoutWork) : withoutWork;
        if (_underFrames < StepDownFrames)
            return;

        _idleMean = Math.Min(_idleMean, _previousUnder);
        _underFrames = 0;
        _previousUnder = double.NaN;
    }

    private void TryStartProbe()
    {
        if (_options.ItemsPerFrameOverride is not null)
        {
            _probePending = false;
            return;
        }

        if (_probeFramesLeft > 0 || _millisecondsSinceProbe < ProbeIntervalMilliseconds)
            return;

        _probePending = false;
        _probeCount++;
        _probeFramesLeft = ProbeMaximumFrames;
        _probeSamples = 0;
        _probeMinimum = double.PositiveInfinity;
        _millisecondsSinceProbe = 0.0;
        _previousIdleAboveLimit = double.NaN;
    }

    private void AdvanceProbe()
    {
        _probeFramesLeft--;
        if (_probeSamples < ProbeFrames && _probeFramesLeft > 0)
            return;

        _probeFramesLeft = 0;
        if (!double.IsFinite(_probeMinimum))
            return;

        // The jitter of the old scene is kept: it is the best guess until idle frames of the new scene teach it.
        SetSceneMean(_probeMinimum, _lastTargetFrameMilliseconds);
        _underFrames = 0;
        _previousUnder = double.NaN;
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
        double excessDecay = Math.Exp(-elapsedMilliseconds / ExcessCostTimeConstantMilliseconds);
        _excessCost *= excessDecay;
        _lastBackoffExcess *= excessDecay;
        _millisecondsSinceBackoff += elapsedMilliseconds;
        _millisecondsSinceRepeatedBackoff += elapsedMilliseconds;
        _millisecondsSinceProbe += elapsedMilliseconds;
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
        if (_millisecondsSinceRepeatedBackoff < RecoverMilliseconds)
        {
            double tolerance = Math.Max(0.0, _options.ToleranceMilliseconds);
            double cost = Math.Max(_costEstimate, _excessCost);
            floor = cost > 0.0
                ? Math.Clamp(tolerance / cost, MinimumItemsPerFrame, 1.0)
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

        if (_probeFramesLeft > 0)
            return 0;

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
