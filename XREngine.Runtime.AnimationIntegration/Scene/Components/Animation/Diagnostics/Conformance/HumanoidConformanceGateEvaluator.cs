namespace XREngine.Components.Animation;

/// <summary>Applies Phase 10 numeric and capability gates without hiding missing comparison data.</summary>
public static class HumanoidConformanceGateEvaluator
{
    /// <summary>Evaluates a comparison report and optional playback observations against one manifest row.</summary>
    public static HumanoidConformanceGateResult Evaluate(
        HumanoidConformanceMatrixCase matrixCase,
        HumanoidPoseAuditComparisonReport comparison,
        HumanoidConformanceObservation? observation = null)
    {
        ArgumentNullException.ThrowIfNull(matrixCase);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(matrixCase.Tolerances);

        var result = new HumanoidConformanceGateResult { MatrixCaseId = matrixCase.Id };
        HumanoidConformanceTolerances tolerances = matrixCase.Tolerances;
        if (observation is null || !float.IsFinite(observation.EngineUnitsPerMeter) || observation.EngineUnitsPerMeter <= 0.0f)
        {
            Add(result, "EngineUnitsPerMeter", "A finite positive engine-units-per-meter scale is required to apply meter tolerances.", observation?.EngineUnitsPerMeter ?? 0.0f, 0.0f);
        }
        else
        {
            float unitsPerMeter = observation.EngineUnitsPerMeter;
            GateMetricInMeters(result, "ProjectedRootTranslation", comparison.ProjectedRootPositionError, tolerances.RootTranslationMeters, unitsPerMeter);
            GateMetricInMeters(result, "TemporalRootTranslation", comparison.TemporalRootMotionTranslationError, tolerances.RootTranslationMeters, unitsPerMeter);
            GateMetricInMeters(result, "HipsLocalPosition", comparison.ComposedHipsLocalPositionError, tolerances.EndpointMeters, unitsPerMeter);
            GateMetricInMeters(result, "Endpoint", comparison.BoneRootSpacePositionError, tolerances.EndpointMeters, unitsPerMeter);
        }

        GateMetric(result, "ProjectedRootRotation", comparison.ProjectedRootRotationErrorDegrees, tolerances.RootRotationDegrees);
        GateMetric(result, "TemporalRootRotation", comparison.TemporalRootMotionRotationErrorDegrees, tolerances.RootRotationDegrees);
        GateMetric(result, "SolvedBodyPosition", comparison.SolvedBodyModelRootPositionErrorMeters, tolerances.RootTranslationMeters);
        GateMetric(result, "SolvedBodyRotation", comparison.SolvedBodyModelRootRotationErrorDegrees, tolerances.RootRotationDegrees);
        GateMetric(result, "HipsLocalRotation", comparison.ComposedHipsLocalRotationErrorDegrees, tolerances.BoneLocalRotationDegrees);
        GateMetric(result, "HipsModelRootPosition", comparison.HipsModelRootPositionErrorMeters, tolerances.EndpointMeters);
        GateMetric(result, "HipsModelRootRotation", comparison.HipsModelRootRotationErrorDegrees, tolerances.BoneLocalRotationDegrees);
        GateMetric(result, "HipsWorldPosition", comparison.HipsWorldPositionErrorMeters, tolerances.EndpointMeters);
        GateMetric(result, "HipsWorldRotation", comparison.HipsWorldRotationErrorDegrees, tolerances.BoneLocalRotationDegrees);
        GateMetric(result, "BoneLocalRotation", comparison.BoneLocalRotationErrorDegrees, tolerances.BoneLocalRotationDegrees);
        GateMetric(result, "BoneModelRootPosition", comparison.BoneModelRootPositionErrorMeters, tolerances.EndpointMeters);
        GateMetric(result, "BoneWorldRotation", comparison.BoneWorldRotationErrorDegrees, tolerances.BoneLocalRotationDegrees);

        if (observation is null)
        {
            Add(result, "ObservationMissing", "Loop drift and capability observations are required; they were not supplied.", 0.0f, 0.0f);
        }
        else
        {
            if (float.IsFinite(observation.EngineUnitsPerMeter) && observation.EngineUnitsPerMeter > 0.0f)
                GateValue(result, "TenLoopDriftTranslation", observation.TenLoopDriftEngineUnits / observation.EngineUnitsPerMeter, tolerances.TenLoopDriftMeters);
            GateValue(result, "TenLoopDriftRotation", observation.TenLoopDriftDegrees, tolerances.TenLoopDriftDegrees);
            HumanoidConformanceCapability missingCapabilities = matrixCase.ExpectedCapabilities & ~observation.ObservedCapabilities;
            if (missingCapabilities != HumanoidConformanceCapability.None)
                Add(result, "Capabilities", $"Required capabilities were not observed: {missingCapabilities}.", (float)missingCapabilities, 0.0f);

            GateObservedBehaviors(result, matrixCase.ExpectedCapabilities, observation);

            for (int i = 0; i < observation.ExplicitFailures.Count; i++)
                Add(result, "ExplicitFailure", observation.ExplicitFailures[i], 1.0f, 0.0f);
            for (int i = 0; i < observation.UnobservedRelevantFields.Count; i++)
                Add(result, "UnobservedField", $"Relevant field was not observed: {observation.UnobservedRelevantFields[i]}.", 1.0f, 0.0f);
        }

        for (int i = 0; i < comparison.Failures.Count; i++)
        {
            HumanoidPoseAuditComparisonFailure failure = comparison.Failures[i];
            string sample = failure.SampleIndex.HasValue ? $" (sample {failure.SampleIndex.Value})" : string.Empty;
            Add(result, $"Comparison:{failure.Code}", failure.Message + sample, 1.0f, 0.0f);
        }

        result.Passed = result.Failures.Count == 0;
        return result;
    }

    private static void GateMetric(HumanoidConformanceGateResult result, string gate, HumanoidPoseAuditMetric metric, float limit)
    {
        if (metric.Count <= 0)
        {
            Add(result, gate, $"{gate} was not compared.", 0.0f, limit);
            return;
        }

        GateValue(result, gate, metric.Max, limit);
    }

    private static void GateMetric(HumanoidConformanceGateResult result, string gate, IReadOnlyList<HumanoidPoseAuditMetricEntry> metrics, float limit)
    {
        if (metrics.Count == 0)
        {
            Add(result, gate, $"{gate} contains no compared entries.", 0.0f, limit);
            return;
        }

        for (int i = 0; i < metrics.Count; i++)
        {
            HumanoidPoseAuditMetricEntry entry = metrics[i];
            if (entry.Metric.Count <= 0)
            {
                Add(result, $"{gate}:{entry.Name}", "Metric was not compared.", 0.0f, limit);
                continue;
            }

            GateValue(result, $"{gate}:{entry.Name}", entry.Metric.Max, limit);
        }
    }

    private static void GateMetricInMeters(
        HumanoidConformanceGateResult result,
        string gate,
        HumanoidPoseAuditMetric metric,
        float limitMeters,
        float engineUnitsPerMeter)
    {
        if (metric.Count <= 0)
        {
            Add(result, gate, $"{gate} was not compared.", 0.0f, limitMeters);
            return;
        }

        GateValue(result, gate, metric.Max / engineUnitsPerMeter, limitMeters);
    }

    private static void GateMetricInMeters(
        HumanoidConformanceGateResult result,
        string gate,
        IReadOnlyList<HumanoidPoseAuditMetricEntry> metrics,
        float limitMeters,
        float engineUnitsPerMeter)
    {
        if (metrics.Count == 0)
        {
            Add(result, gate, $"{gate} contains no compared entries.", 0.0f, limitMeters);
            return;
        }

        for (int i = 0; i < metrics.Count; i++)
        {
            HumanoidPoseAuditMetricEntry entry = metrics[i];
            if (entry.Metric.Count <= 0)
            {
                Add(result, $"{gate}:{entry.Name}", "Metric was not compared.", 0.0f, limitMeters);
                continue;
            }

            GateValue(result, $"{gate}:{entry.Name}", entry.Metric.Max / engineUnitsPerMeter, limitMeters);
        }
    }

    private static void GateObservedBehaviors(
        HumanoidConformanceGateResult result,
        HumanoidConformanceCapability expected,
        HumanoidConformanceObservation observation)
    {
        if (expected.HasFlag(HumanoidConformanceCapability.Events) && observation.ObservedEventCount <= 0)
            Add(result, "Events", "Expected event behavior was not observed.", observation.ObservedEventCount, 1.0f);
        if (expected.HasFlag(HumanoidConformanceCapability.ObjectReferenceBindings) && observation.ObservedObjectReferenceBindingCount <= 0)
            Add(result, "ObjectReferenceBindings", "Expected object-reference binding behavior was not observed.", observation.ObservedObjectReferenceBindingCount, 1.0f);
        if (expected.HasFlag(HumanoidConformanceCapability.InverseKinematics) && observation.InverseKinematicsApplied is not true)
            Add(result, "InverseKinematics", "Expected IK application outcome was not observed.", 0.0f, 1.0f);
        if (expected.HasFlag(HumanoidConformanceCapability.NoInverseKinematics) && observation.InverseKinematicsDisabled is not true)
            Add(result, "NoInverseKinematics", "Expected no-IK outcome was not observed.", 0.0f, 1.0f);
        if (expected.HasFlag(HumanoidConformanceCapability.FootContact) && observation.ObservedFootContactCount <= 0)
            Add(result, "FootContact", "Expected foot-contact behavior was not observed.", observation.ObservedFootContactCount, 1.0f);
    }

    private static void GateValue(HumanoidConformanceGateResult result, string gate, float actual, float limit)
    {
        if (!float.IsFinite(actual))
        {
            Add(result, gate, "Measured value is not finite.", actual, limit);
            return;
        }

        if (actual > limit)
            Add(result, gate, $"Measured value {actual:G9} exceeds limit {limit:G9}.", actual, limit);
    }

    private static void Add(HumanoidConformanceGateResult result, string gate, string message, float actual, float limit)
        => result.Failures.Add(new HumanoidConformanceGateFailure
        {
            Gate = gate,
            Message = message,
            Actual = actual,
            Limit = limit,
        });
}
