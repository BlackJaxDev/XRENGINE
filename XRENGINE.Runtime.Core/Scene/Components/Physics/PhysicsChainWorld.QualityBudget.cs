namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    private struct QualityRuntimeState
    {
        public PhysicsChainQualityTier RequestedTier;
        public PhysicsChainQualityDecisionReason Reason;
        public long TierSinceFrame;
        public long LastTransitionFrame;
        public bool HasAutomaticTransition;
        public bool HasAutomaticObservation;
        public PhysicsChainAutomaticQualityObservation AutomaticObservation;
        public bool HasDelayedFeedback;
        public bool FeedbackRequiresPromotion;
        public long LastFeedbackSourceFrame;
        public double SmoothedMillisecondsPerWorkUnit;
        public float SmoothedNormalizedError;
    }

    private struct QualityCandidate
    {
        public int SlotIndex;
        public int BaseWorkUnits;
        public int Importance;
        public PhysicsChainAutomaticRelevance Relevance;
        public PhysicsChainQualityTier RequestedTier;
        public PhysicsChainQualityTier EffectiveTier;
        public bool UsesGpu;
        public bool ChangedThisFrame;
        public bool DeferredByResidency;
        public bool DeferredByTransitionLimit;
    }

    private readonly List<QualityCandidate> _qualityCandidates = [];
    private long _qualityFrame;
    private long _automaticCpuWorkUnitBudget = 800_000L;
    private long _automaticGpuWorkUnitBudget = 800_000L;
    private int _maximumAutomaticQualityTransitionsPerFrame = 8;
    private int _minimumAutomaticTierResidenceFrames = 30;
    private int _automaticQualityPromotionHysteresisPercent = 20;

    /// <summary>
    /// CPU physics-chain budget in normalized work units. One unit represents
    /// the estimated cost of one chain at 7.5 Hz; strict quality costs eight
    /// units. Fixed-tier chains neither consume nor are changed by this pool.
    /// </summary>
    public long AutomaticCpuWorkUnitBudget
    {
        get => _automaticCpuWorkUnitBudget;
        set => _automaticCpuWorkUnitBudget = Math.Max(value, 0L);
    }

    /// <summary>
    /// GPU physics-chain budget in the same normalized units as the CPU
    /// budget. CPU and GPU pressure are evaluated independently.
    /// </summary>
    public long AutomaticGpuWorkUnitBudget
    {
        get => _automaticGpuWorkUnitBudget;
        set => _automaticGpuWorkUnitBudget = Math.Max(value, 0L);
    }

    /// <summary>
    /// Maximum automatic tier transitions permitted in one world frame.
    /// Authored fixed-tier changes are applied immediately and do not consume
    /// this limit.
    /// </summary>
    public int MaximumAutomaticQualityTransitionsPerFrame
    {
        get => _maximumAutomaticQualityTransitionsPerFrame;
        set => _maximumAutomaticQualityTransitionsPerFrame = Math.Max(value, 0);
    }

    /// <summary>
    /// Minimum frames an automatically changed chain must remain in its tier
    /// before another automatic promotion or demotion.
    /// </summary>
    public int MinimumAutomaticTierResidenceFrames
    {
        get => _minimumAutomaticTierResidenceFrames;
        set => _minimumAutomaticTierResidenceFrames = Math.Max(value, 0);
    }

    /// <summary>
    /// Percentage of budget held as promotion headroom. Demotion begins at the
    /// hard budget while promotion requires this additional margin.
    /// </summary>
    public int AutomaticQualityPromotionHysteresisPercent
    {
        get => _automaticQualityPromotionHysteresisPercent;
        set => _automaticQualityPromotionHysteresisPercent = Math.Clamp(value, 0, 90);
    }

    /// <summary>
    /// Aggregate diagnostics from the most recent controller evaluation.
    /// </summary>
    public PhysicsChainQualityBudgetDiagnostics QualityBudgetDiagnostics { get; private set; }

    /// <summary>
    /// Resolves per-chain quality diagnostics through a generation-safe runtime
    /// handle.
    /// </summary>
    public bool TryGetQualityDiagnostics(
        PhysicsChainRuntimeHandle handle,
        out PhysicsChainQualityDiagnostics diagnostics)
    {
        diagnostics = default;
        if (!handle.IsValid || (uint)handle.Slot >= (uint)_slots.Count)
            return false;

        RuntimeSlot slot = _slots[handle.Slot];
        if (slot.Generation != handle.Generation || slot.Component is null)
            return false;

        QualityRuntimeState state = slot.QualityState;
        diagnostics = new PhysicsChainQualityDiagnostics(
            state.RequestedTier,
            slot.Component.EffectiveQualityTier,
            state.Reason,
            Math.Max(0L, _qualityFrame - state.TierSinceFrame),
            state.LastTransitionFrame,
            state.HasDelayedFeedback,
            state.LastFeedbackSourceFrame,
            state.SmoothedMillisecondsPerWorkUnit,
            state.SmoothedNormalizedError);
        return true;
    }

    /// <summary>
    /// Supplies generation-safe renderer relevance for an automatic chain.
    /// The observation is retained until replaced or the instance is removed.
    /// </summary>
    public bool TrySetAutomaticQualityObservation(
        PhysicsChainRuntimeHandle handle,
        in PhysicsChainAutomaticQualityObservation observation)
    {
        if (!observation.IsValid
            || !handle.IsValid
            || (uint)handle.Slot >= (uint)_slots.Count)
            return false;

        RuntimeSlot slot = _slots[handle.Slot];
        PhysicsChainComponent? component = slot.Component;
        if (slot.Generation != handle.Generation || component is null)
            return false;

        QualityRuntimeState state = slot.QualityState;
        bool tierChanged = state.HasAutomaticObservation
            && PhysicsChainAutomaticQualityEvaluation.ResolveVisibleTier(
                state.AutomaticObservation,
                component.AutomaticQualityImportance)
            != PhysicsChainAutomaticQualityEvaluation.ResolveVisibleTier(
                observation,
                component.AutomaticQualityImportance);
        state.AutomaticObservation = observation;
        state.HasAutomaticObservation = true;
        slot.QualityState = state;
        _slots[handle.Slot] = slot;

        component.SetRuntimeVisibility(observation.Visible);
        if (tierChanged)
            component.Wake(PhysicsChainWakeReason.RelevanceChanged);
        return true;
    }

    /// <summary>Returns the latest renderer relevance observation, if supplied.</summary>
    public bool TryGetAutomaticQualityObservation(
        PhysicsChainRuntimeHandle handle,
        out PhysicsChainAutomaticQualityObservation observation)
    {
        observation = default;
        if (!handle.IsValid || (uint)handle.Slot >= (uint)_slots.Count)
            return false;
        RuntimeSlot slot = _slots[handle.Slot];
        if (slot.Generation != handle.Generation || !slot.QualityState.HasAutomaticObservation)
            return false;
        observation = slot.QualityState.AutomaticObservation;
        return true;
    }

    private QualityRuntimeState CreateInitialQualityState(PhysicsChainComponent component)
    {
        PhysicsChainQualityTier requested = ResolveRequestedTier(component);
        return new QualityRuntimeState
        {
            RequestedTier = requested,
            Reason = component.QualityTier == PhysicsChainQualityTier.Automatic
                ? PhysicsChainQualityDecisionReason.WithinBudget
                : PhysicsChainQualityDecisionReason.AuthoredFixedTier,
            TierSinceFrame = _qualityFrame,
            LastTransitionFrame = -1L,
            LastFeedbackSourceFrame = -1L,
        };
    }

    private void EvaluateQualityBudget()
    {
        ++_qualityFrame;
        UpdateEffectiveAutomaticBudgets();
        _qualityCandidates.Clear();

        long cpuWorkUnits = 0L;
        long gpuWorkUnits = 0L;
        int automaticChainCount = 0;

        for (int i = 0; i < _liveSlots.Count; ++i)
        {
            int slotIndex = _liveSlots[i];
            RuntimeSlot slot = _slots[slotIndex];
            PhysicsChainComponent? component = slot.Component;
            if (component is not { IsActiveInHierarchy: true })
                continue;

            int baseWorkUnits = Math.Max(component.EstimatedWorldWork, 1);
            if (component.QualityTier != PhysicsChainQualityTier.Automatic)
            {
                ApplyFixedTier(component, ref slot.QualityState, component.QualityTier);
                _slots[slotIndex] = slot;
                continue;
            }

            ++automaticChainCount;
            component.AdvanceAutomaticQualityFrame();
            PhysicsChainQualityTier requestedTier = ResolveRequestedTier(
                component,
                slot.QualityState.HasAutomaticObservation,
                slot.QualityState.AutomaticObservation);
            if (slot.QualityState.FeedbackRequiresPromotion)
                requestedTier = NextHigherCadence(requestedTier);
            slot.QualityState.RequestedTier = requestedTier;
            slot.QualityState.Reason = requestedTier == component.EffectiveQualityTier
                ? slot.QualityState.HasAutomaticObservation
                    ? PhysicsChainQualityDecisionReason.RelevancePolicy
                    : ResolveSteadyReason(component.AutomaticQualityRelevance)
                : PhysicsChainQualityDecisionReason.WithinBudget;
            _slots[slotIndex] = slot;

            _qualityCandidates.Add(new QualityCandidate
            {
                SlotIndex = slotIndex,
                BaseWorkUnits = baseWorkUnits,
                Importance = component.AutomaticQualityImportance,
                Relevance = component.AutomaticQualityRelevance,
                RequestedTier = requestedTier,
                EffectiveTier = component.EffectiveQualityTier,
                UsesGpu = component.UseGPU,
            });
            AddWorkUnits(
                component.UseGPU,
                GetEffectiveWorkUnits(baseWorkUnits, component.EffectiveQualityTier),
                ref cpuWorkUnits,
                ref gpuWorkUnits);
        }

        int transitions = 0;
        int deferredByResidency = 0;
        int deferredByTransitionLimit = 0;

        _qualityCandidates.Sort(static (left, right) => CompareForDemotion(left, right));
        ApplyRequestedDemotions(
            ref cpuWorkUnits,
            ref gpuWorkUnits,
            ref transitions,
            ref deferredByResidency,
            ref deferredByTransitionLimit);
        ApplyBudgetDemotions(
            ref cpuWorkUnits,
            ref gpuWorkUnits,
            ref transitions,
            ref deferredByResidency,
            ref deferredByTransitionLimit);

        _qualityCandidates.Sort(static (left, right) => CompareForPromotion(left, right));
        ApplyPromotions(
            ref cpuWorkUnits,
            ref gpuWorkUnits,
            ref transitions,
            ref deferredByResidency,
            ref deferredByTransitionLimit);

        QualityBudgetDiagnostics = new PhysicsChainQualityBudgetDiagnostics(
            _qualityFrame,
            AutomaticCpuWorkUnitBudget,
            _effectiveAutomaticCpuWorkUnitBudget,
            cpuWorkUnits,
            AutomaticGpuWorkUnitBudget,
            _effectiveAutomaticGpuWorkUnitBudget,
            gpuWorkUnits,
            automaticChainCount,
            transitions,
            deferredByResidency,
            deferredByTransitionLimit,
            _acceptedQualityFeedbackSamples,
            _rejectedQualityFeedbackSamples,
            _lastCpuFeedbackSourceFrame,
            _lastGpuFeedbackSourceFrame,
            _smoothedCpuMillisecondsPerWorkUnit,
            _smoothedGpuMillisecondsPerWorkUnit);
    }

    private void ApplyRequestedDemotions(
        ref long cpuWorkUnits,
        ref long gpuWorkUnits,
        ref int transitions,
        ref int deferredByResidency,
        ref int deferredByTransitionLimit)
    {
        for (int i = 0; i < _qualityCandidates.Count; ++i)
        {
            QualityCandidate candidate = _qualityCandidates[i];
            if (GetTierRank(candidate.EffectiveTier) >= GetTierRank(candidate.RequestedTier))
                continue;

            TryApplyAutomaticTransition(
                ref candidate,
                candidate.RequestedTier,
                PhysicsChainQualityDecisionReason.RelevancePolicy,
                ref cpuWorkUnits,
                ref gpuWorkUnits,
                ref transitions,
                ref deferredByResidency,
                ref deferredByTransitionLimit);
            _qualityCandidates[i] = candidate;
        }
    }

    private void ApplyBudgetDemotions(
        ref long cpuWorkUnits,
        ref long gpuWorkUnits,
        ref int transitions,
        ref int deferredByResidency,
        ref int deferredByTransitionLimit)
    {
        for (int i = 0; i < _qualityCandidates.Count; ++i)
        {
            QualityCandidate candidate = _qualityCandidates[i];
            if (candidate.ChangedThisFrame || !IsPoolOverBudget(candidate.UsesGpu, cpuWorkUnits, gpuWorkUnits))
                continue;

            PhysicsChainQualityTier targetTier = NextLowerCadence(candidate.EffectiveTier);
            if (targetTier == candidate.EffectiveTier || targetTier == PhysicsChainQualityTier.Sleep)
                continue;

            TryApplyAutomaticTransition(
                ref candidate,
                targetTier,
                candidate.UsesGpu
                    ? PhysicsChainQualityDecisionReason.GpuBudgetPressure
                    : PhysicsChainQualityDecisionReason.CpuBudgetPressure,
                ref cpuWorkUnits,
                ref gpuWorkUnits,
                ref transitions,
                ref deferredByResidency,
                ref deferredByTransitionLimit);
            _qualityCandidates[i] = candidate;
        }
    }

    private void ApplyPromotions(
        ref long cpuWorkUnits,
        ref long gpuWorkUnits,
        ref int transitions,
        ref int deferredByResidency,
        ref int deferredByTransitionLimit)
    {
        long cpuPromotionLimit = GetPromotionLimit(_effectiveAutomaticCpuWorkUnitBudget);
        long gpuPromotionLimit = GetPromotionLimit(_effectiveAutomaticGpuWorkUnitBudget);

        for (int i = 0; i < _qualityCandidates.Count; ++i)
        {
            QualityCandidate candidate = _qualityCandidates[i];
            if (candidate.ChangedThisFrame
                || GetTierRank(candidate.EffectiveTier) <= GetTierRank(candidate.RequestedTier))
                continue;

            PhysicsChainQualityTier targetTier = NextHigherCadence(candidate.EffectiveTier);
            if (GetTierRank(targetTier) < GetTierRank(candidate.RequestedTier))
                targetTier = candidate.RequestedTier;

            long currentCost = GetEffectiveWorkUnits(candidate.BaseWorkUnits, candidate.EffectiveTier);
            long targetCost = GetEffectiveWorkUnits(candidate.BaseWorkUnits, targetTier);
            long projectedWork = SaturatingAdd(
                candidate.UsesGpu ? gpuWorkUnits : cpuWorkUnits,
                Math.Max(0L, targetCost - currentCost));
            long promotionLimit = candidate.UsesGpu ? gpuPromotionLimit : cpuPromotionLimit;
            if (projectedWork > promotionLimit)
            {
                SetCandidateReason(
                    candidate.SlotIndex,
                    candidate.UsesGpu
                        ? PhysicsChainQualityDecisionReason.GpuBudgetPressure
                        : PhysicsChainQualityDecisionReason.CpuBudgetPressure);
                continue;
            }

            TryApplyAutomaticTransition(
                ref candidate,
                targetTier,
                PhysicsChainQualityDecisionReason.PromotionHeadroom,
                ref cpuWorkUnits,
                ref gpuWorkUnits,
                ref transitions,
                ref deferredByResidency,
                ref deferredByTransitionLimit);
            _qualityCandidates[i] = candidate;
        }
    }

    private void TryApplyAutomaticTransition(
        ref QualityCandidate candidate,
        PhysicsChainQualityTier targetTier,
        PhysicsChainQualityDecisionReason reason,
        ref long cpuWorkUnits,
        ref long gpuWorkUnits,
        ref int transitions,
        ref int deferredByResidency,
        ref int deferredByTransitionLimit)
    {
        if (candidate.ChangedThisFrame || candidate.EffectiveTier == targetTier)
            return;

        RuntimeSlot slot = _slots[candidate.SlotIndex];
        PhysicsChainComponent? component = slot.Component;
        if (component is null)
            return;

        if (transitions >= MaximumAutomaticQualityTransitionsPerFrame)
        {
            slot.QualityState.Reason = PhysicsChainQualityDecisionReason.TransitionLimit;
            _slots[candidate.SlotIndex] = slot;
            if (!candidate.DeferredByTransitionLimit)
            {
                candidate.DeferredByTransitionLimit = true;
                ++deferredByTransitionLimit;
            }
            return;
        }

        long residenceFrames = Math.Max(0L, _qualityFrame - slot.QualityState.TierSinceFrame);
        if (slot.QualityState.HasAutomaticTransition
            && residenceFrames < MinimumAutomaticTierResidenceFrames)
        {
            slot.QualityState.Reason = PhysicsChainQualityDecisionReason.MinimumResidency;
            _slots[candidate.SlotIndex] = slot;
            if (!candidate.DeferredByResidency)
            {
                candidate.DeferredByResidency = true;
                ++deferredByResidency;
            }
            return;
        }

        long previousCost = GetEffectiveWorkUnits(candidate.BaseWorkUnits, candidate.EffectiveTier);
        long targetCost = GetEffectiveWorkUnits(candidate.BaseWorkUnits, targetTier);
        ReplaceWorkUnits(
            candidate.UsesGpu,
            previousCost,
            targetCost,
            ref cpuWorkUnits,
            ref gpuWorkUnits);

        component.SetEffectiveQualityTier(targetTier);
        slot.QualityState.Reason = reason;
        slot.QualityState.TierSinceFrame = _qualityFrame;
        slot.QualityState.LastTransitionFrame = _qualityFrame;
        slot.QualityState.HasAutomaticTransition = true;
        _slots[candidate.SlotIndex] = slot;

        candidate.EffectiveTier = targetTier;
        candidate.ChangedThisFrame = true;
        ++transitions;
    }

    private void ApplyFixedTier(
        PhysicsChainComponent component,
        ref QualityRuntimeState state,
        PhysicsChainQualityTier tier)
    {
        state.RequestedTier = tier;
        state.Reason = PhysicsChainQualityDecisionReason.AuthoredFixedTier;
        state.HasAutomaticTransition = false;
        if (component.EffectiveQualityTier == tier)
            return;

        component.SetEffectiveQualityTier(tier);
        state.TierSinceFrame = _qualityFrame;
        state.LastTransitionFrame = _qualityFrame;
    }

    private void SetCandidateReason(int slotIndex, PhysicsChainQualityDecisionReason reason)
    {
        RuntimeSlot slot = _slots[slotIndex];
        slot.QualityState.Reason = reason;
        _slots[slotIndex] = slot;
    }

    private bool IsPoolOverBudget(bool usesGpu, long cpuWorkUnits, long gpuWorkUnits)
        => usesGpu
            ? gpuWorkUnits > _effectiveAutomaticGpuWorkUnitBudget
            : cpuWorkUnits > _effectiveAutomaticCpuWorkUnitBudget;

    private long GetPromotionLimit(long budget)
    {
        long percentage = AutomaticQualityPromotionHysteresisPercent;
        long reserved = (budget / 100L * percentage) + (budget % 100L * percentage / 100L);
        return budget - reserved;
    }

    private static PhysicsChainQualityTier ResolveRequestedTier(
        PhysicsChainComponent component,
        bool hasAutomaticObservation = false,
        PhysicsChainAutomaticQualityObservation automaticObservation = default)
    {
        if (component.QualityTier != PhysicsChainQualityTier.Automatic)
            return component.QualityTier;

        PhysicsChainQualityTier visibleTier = hasAutomaticObservation
            ? PhysicsChainAutomaticQualityEvaluation.ResolveVisibleTier(
                automaticObservation,
                component.AutomaticQualityImportance)
            : component.AutomaticQualityRelevance switch
            {
                PhysicsChainAutomaticRelevance.Distant => PhysicsChainQualityTier.Hz30,
                PhysicsChainAutomaticRelevance.Irrelevant => PhysicsChainQualityTier.Sleep,
                _ => PhysicsChainQualityTier.Strict,
            };
        return component.ResolveAutomaticOffscreenTier(visibleTier);
    }

    private static PhysicsChainQualityDecisionReason ResolveSteadyReason(
        PhysicsChainAutomaticRelevance relevance)
        => relevance is PhysicsChainAutomaticRelevance.Distant or PhysicsChainAutomaticRelevance.Irrelevant
            ? PhysicsChainQualityDecisionReason.RelevancePolicy
            : PhysicsChainQualityDecisionReason.WithinBudget;

    private static PhysicsChainQualityTier NextLowerCadence(PhysicsChainQualityTier tier)
        => tier switch
        {
            PhysicsChainQualityTier.Strict => PhysicsChainQualityTier.Hz30,
            PhysicsChainQualityTier.Hz30 => PhysicsChainQualityTier.Hz15,
            PhysicsChainQualityTier.Hz15 => PhysicsChainQualityTier.Hz7_5,
            _ => tier,
        };

    private static PhysicsChainQualityTier NextHigherCadence(PhysicsChainQualityTier tier)
        => tier switch
        {
            PhysicsChainQualityTier.Sleep => PhysicsChainQualityTier.Hz7_5,
            PhysicsChainQualityTier.Hz7_5 => PhysicsChainQualityTier.Hz15,
            PhysicsChainQualityTier.Hz15 => PhysicsChainQualityTier.Hz30,
            PhysicsChainQualityTier.Hz30 => PhysicsChainQualityTier.Strict,
            _ => tier,
        };

    private static int GetTierRank(PhysicsChainQualityTier tier)
        => tier switch
        {
            PhysicsChainQualityTier.Strict => 0,
            PhysicsChainQualityTier.Hz30 => 1,
            PhysicsChainQualityTier.Hz15 => 2,
            PhysicsChainQualityTier.Hz7_5 => 3,
            PhysicsChainQualityTier.Sleep => 4,
            _ => 0,
        };

    private static long GetEffectiveWorkUnits(int baseWorkUnits, PhysicsChainQualityTier tier)
    {
        int multiplier = tier switch
        {
            PhysicsChainQualityTier.Strict => 8,
            PhysicsChainQualityTier.Hz30 => 4,
            PhysicsChainQualityTier.Hz15 => 2,
            PhysicsChainQualityTier.Hz7_5 => 1,
            PhysicsChainQualityTier.Sleep => 0,
            _ => 8,
        };
        return baseWorkUnits > long.MaxValue / Math.Max(multiplier, 1)
            ? long.MaxValue
            : (long)baseWorkUnits * multiplier;
    }

    private static int CompareForDemotion(QualityCandidate left, QualityCandidate right)
    {
        int relevance = right.Relevance.CompareTo(left.Relevance);
        if (relevance != 0)
            return relevance;

        int importance = left.Importance.CompareTo(right.Importance);
        return importance != 0 ? importance : left.SlotIndex.CompareTo(right.SlotIndex);
    }

    private static int CompareForPromotion(QualityCandidate left, QualityCandidate right)
    {
        int relevance = left.Relevance.CompareTo(right.Relevance);
        if (relevance != 0)
            return relevance;

        int importance = right.Importance.CompareTo(left.Importance);
        return importance != 0 ? importance : left.SlotIndex.CompareTo(right.SlotIndex);
    }

    private static void AddWorkUnits(
        bool usesGpu,
        long workUnits,
        ref long cpuWorkUnits,
        ref long gpuWorkUnits)
    {
        if (usesGpu)
            gpuWorkUnits = SaturatingAdd(gpuWorkUnits, workUnits);
        else
            cpuWorkUnits = SaturatingAdd(cpuWorkUnits, workUnits);
    }

    private static void ReplaceWorkUnits(
        bool usesGpu,
        long previousWorkUnits,
        long targetWorkUnits,
        ref long cpuWorkUnits,
        ref long gpuWorkUnits)
    {
        if (usesGpu)
            gpuWorkUnits = SaturatingAdd(Math.Max(0L, gpuWorkUnits - previousWorkUnits), targetWorkUnits);
        else
            cpuWorkUnits = SaturatingAdd(Math.Max(0L, cpuWorkUnits - previousWorkUnits), targetWorkUnits);
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;
}
