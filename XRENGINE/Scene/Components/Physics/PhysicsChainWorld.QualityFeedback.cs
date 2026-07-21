namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    private double _automaticCpuTimeBudgetMilliseconds = double.PositiveInfinity;
    private double _automaticGpuTimeBudgetMilliseconds = double.PositiveInfinity;
    private int _qualityFeedbackSmoothingPermille = 250;
    private int _maximumQualityFeedbackAgeFrames = 8;
    private float _qualityFeedbackPromotionError = 1.0f;
    private float _qualityFeedbackRecoveryError = 0.75f;
    private bool _hasCpuFeedback;
    private bool _hasGpuFeedback;
    private double _smoothedCpuMillisecondsPerWorkUnit;
    private double _smoothedGpuMillisecondsPerWorkUnit;
    private long _lastCpuFeedbackSourceFrame = -1L;
    private long _lastGpuFeedbackSourceFrame = -1L;
    private long _acceptedQualityFeedbackSamples;
    private long _rejectedQualityFeedbackSamples;
    private long _effectiveAutomaticCpuWorkUnitBudget;
    private long _effectiveAutomaticGpuWorkUnitBudget;

    /// <summary>
    /// Optional CPU time limit used to convert delayed timings into an
    /// effective normalized-work budget. Positive infinity disables timing
    /// calibration while retaining the configured work-unit limit.
    /// </summary>
    public double AutomaticCpuTimeBudgetMilliseconds
    {
        get => _automaticCpuTimeBudgetMilliseconds;
        set => _automaticCpuTimeBudgetMilliseconds = NormalizeTimeBudget(value);
    }

    /// <summary>GPU counterpart of <see cref="AutomaticCpuTimeBudgetMilliseconds"/>.</summary>
    public double AutomaticGpuTimeBudgetMilliseconds
    {
        get => _automaticGpuTimeBudgetMilliseconds;
        set => _automaticGpuTimeBudgetMilliseconds = NormalizeTimeBudget(value);
    }

    /// <summary>
    /// Fixed-point exponential smoothing weight for each accepted sample.
    /// Using an integer permille makes the update rule repeatable.
    /// </summary>
    public int QualityFeedbackSmoothingPermille
    {
        get => _qualityFeedbackSmoothingPermille;
        set => _qualityFeedbackSmoothingPermille = Math.Clamp(value, 1, 1000);
    }

    /// <summary>Maximum permitted delay between source and controller frames.</summary>
    public int MaximumQualityFeedbackAgeFrames
    {
        get => _maximumQualityFeedbackAgeFrames;
        set => _maximumQualityFeedbackAgeFrames = Math.Max(value, 1);
    }

    /// <summary>Error ratio at which an automatic chain requests one safer tier.</summary>
    public float QualityFeedbackPromotionError
    {
        get => _qualityFeedbackPromotionError;
        set
        {
            if (!float.IsFinite(value) || value < 0.0f)
                return;
            _qualityFeedbackPromotionError = MathF.Max(value, _qualityFeedbackRecoveryError);
        }
    }

    /// <summary>
    /// Lower error ratio required to release a feedback-driven promotion.
    /// This separate threshold prevents sample noise from toggling tiers.
    /// </summary>
    public float QualityFeedbackRecoveryError
    {
        get => _qualityFeedbackRecoveryError;
        set
        {
            if (!float.IsFinite(value) || value < 0.0f)
                return;
            _qualityFeedbackRecoveryError = MathF.Min(value, _qualityFeedbackPromotionError);
        }
    }

    /// <summary>
    /// Accepts a completed prior-frame timing/error sample. Handles, backend,
    /// frame age, ordering, and automatic-quality authorization are all checked
    /// before controller state changes.
    /// </summary>
    public bool TrySubmitDelayedQualityFeedback(
        PhysicsChainRuntimeHandle handle,
        in PhysicsChainQualityFeedbackSample sample,
        out PhysicsChainQualityFeedbackRejectionReason rejectionReason)
    {
        rejectionReason = ValidateQualityFeedback(handle, sample, out RuntimeSlot slot);
        if (rejectionReason != PhysicsChainQualityFeedbackRejectionReason.None)
        {
            ++_rejectedQualityFeedbackSamples;
            return false;
        }

        double millisecondsPerWorkUnit = sample.ElapsedMilliseconds / sample.AutomaticWorkUnits;
        QualityRuntimeState state = slot.QualityState;
        if (state.HasDelayedFeedback)
        {
            state.SmoothedMillisecondsPerWorkUnit = Smooth(
                state.SmoothedMillisecondsPerWorkUnit,
                millisecondsPerWorkUnit);
            state.SmoothedNormalizedError = Smooth(
                state.SmoothedNormalizedError,
                sample.NormalizedError);
        }
        else
        {
            state.HasDelayedFeedback = true;
            state.SmoothedMillisecondsPerWorkUnit = millisecondsPerWorkUnit;
            state.SmoothedNormalizedError = sample.NormalizedError;
        }

        state.LastFeedbackSourceFrame = sample.SourceFrame;
        if (state.FeedbackRequiresPromotion)
            state.FeedbackRequiresPromotion = state.SmoothedNormalizedError > QualityFeedbackRecoveryError;
        else
            state.FeedbackRequiresPromotion = state.SmoothedNormalizedError >= QualityFeedbackPromotionError;
        slot.QualityState = state;
        _slots[handle.Slot] = slot;

        if (sample.Backend == PhysicsChainQualityFeedbackBackend.Gpu)
        {
            _smoothedGpuMillisecondsPerWorkUnit = _hasGpuFeedback
                ? Smooth(_smoothedGpuMillisecondsPerWorkUnit, millisecondsPerWorkUnit)
                : millisecondsPerWorkUnit;
            _hasGpuFeedback = true;
            _lastGpuFeedbackSourceFrame = Math.Max(_lastGpuFeedbackSourceFrame, sample.SourceFrame);
        }
        else
        {
            _smoothedCpuMillisecondsPerWorkUnit = _hasCpuFeedback
                ? Smooth(_smoothedCpuMillisecondsPerWorkUnit, millisecondsPerWorkUnit)
                : millisecondsPerWorkUnit;
            _hasCpuFeedback = true;
            _lastCpuFeedbackSourceFrame = Math.Max(_lastCpuFeedbackSourceFrame, sample.SourceFrame);
        }

        ++_acceptedQualityFeedbackSamples;
        return true;
    }

    private PhysicsChainQualityFeedbackRejectionReason ValidateQualityFeedback(
        PhysicsChainRuntimeHandle handle,
        in PhysicsChainQualityFeedbackSample sample,
        out RuntimeSlot slot)
    {
        slot = default;
        if (!sample.IsValid)
            return PhysicsChainQualityFeedbackRejectionReason.InvalidSample;
        if (!handle.IsValid || (uint)handle.Slot >= (uint)_slots.Count)
            return PhysicsChainQualityFeedbackRejectionReason.InvalidHandle;

        slot = _slots[handle.Slot];
        if (slot.Generation != handle.Generation || slot.Component is null)
            return PhysicsChainQualityFeedbackRejectionReason.StaleGeneration;
        if (slot.Component.QualityTier != PhysicsChainQualityTier.Automatic)
            return PhysicsChainQualityFeedbackRejectionReason.NotAutomatic;
        if ((sample.Backend == PhysicsChainQualityFeedbackBackend.Gpu) != slot.Component.UseGPU)
            return PhysicsChainQualityFeedbackRejectionReason.BackendMismatch;
        if (sample.SourceFrame >= _qualityFrame)
            return PhysicsChainQualityFeedbackRejectionReason.CurrentOrFutureFrame;
        if (_qualityFrame - sample.SourceFrame > MaximumQualityFeedbackAgeFrames)
            return PhysicsChainQualityFeedbackRejectionReason.Expired;
        if (sample.SourceFrame <= slot.QualityState.LastFeedbackSourceFrame)
            return PhysicsChainQualityFeedbackRejectionReason.OutOfOrder;
        return PhysicsChainQualityFeedbackRejectionReason.None;
    }

    private void UpdateEffectiveAutomaticBudgets()
    {
        _effectiveAutomaticCpuWorkUnitBudget = ResolveEffectiveBudget(
            AutomaticCpuWorkUnitBudget,
            AutomaticCpuTimeBudgetMilliseconds,
            _hasCpuFeedback,
            _smoothedCpuMillisecondsPerWorkUnit);
        _effectiveAutomaticGpuWorkUnitBudget = ResolveEffectiveBudget(
            AutomaticGpuWorkUnitBudget,
            AutomaticGpuTimeBudgetMilliseconds,
            _hasGpuFeedback,
            _smoothedGpuMillisecondsPerWorkUnit);
    }

    private long ResolveEffectiveBudget(
        long configuredWorkUnits,
        double timeBudgetMilliseconds,
        bool hasFeedback,
        double millisecondsPerWorkUnit)
    {
        if (!hasFeedback || double.IsPositiveInfinity(timeBudgetMilliseconds))
            return configuredWorkUnits;
        if (timeBudgetMilliseconds <= 0.0 || millisecondsPerWorkUnit <= 0.0)
            return 0L;

        double timedUnits = Math.Floor(timeBudgetMilliseconds / millisecondsPerWorkUnit);
        long calibrated = timedUnits >= long.MaxValue ? long.MaxValue : (long)Math.Max(0.0, timedUnits);
        return Math.Min(configuredWorkUnits, calibrated);
    }

    private double Smooth(double previous, double sample)
        => previous + ((sample - previous) * QualityFeedbackSmoothingPermille / 1000.0);

    private float Smooth(float previous, float sample)
        => previous + ((sample - previous) * QualityFeedbackSmoothingPermille / 1000.0f);

    private static double NormalizeTimeBudget(double value)
        => double.IsPositiveInfinity(value) ? value : double.IsFinite(value) ? Math.Max(0.0, value) : 0.0;
}
