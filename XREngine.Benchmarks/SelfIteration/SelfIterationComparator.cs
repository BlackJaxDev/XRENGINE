namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Applies invariant, identity, per-metric, and aggregate improvement gates.
/// </summary>
public sealed class SelfIterationComparator
{
    public SelfIterationComparisonResult Compare(
        IReadOnlyList<SelfIterationScenarioMeasurement> baseline,
        IReadOnlyList<SelfIterationScenarioMeasurement> candidate,
        SelfIterationAcceptanceConfiguration acceptance)
    {
        var reasons = new List<string>();
        var comparisons = new List<SelfIterationMetricComparison>();
        double weightedImprovement = 0.0;
        double totalWeight = 0.0;
        bool anyMaterialImprovement = false;

        Dictionary<string, SelfIterationScenarioMeasurement> candidateByName =
            candidate.ToDictionary(
                static measurement => measurement.ScenarioName,
                StringComparer.OrdinalIgnoreCase);
        foreach (SelfIterationScenarioMeasurement baselineMeasurement in baseline)
        {
            if (!candidateByName.TryGetValue(
                    baselineMeasurement.ScenarioName,
                    out SelfIterationScenarioMeasurement? candidateMeasurement))
            {
                reasons.Add($"Candidate omitted scenario '{baselineMeasurement.ScenarioName}'.");
                continue;
            }

            reasons.AddRange(candidateMeasurement.Validate(acceptance));
            if (acceptance.RequireStableWorkloadIdentity &&
                !baselineMeasurement.WorkloadIdentityHashes.SequenceEqual(
                    candidateMeasurement.WorkloadIdentityHashes,
                    StringComparer.Ordinal))
            {
                reasons.Add(
                    $"{baselineMeasurement.ScenarioName}: candidate workload identity differs from baseline.");
            }

            foreach (SelfIterationMetricRule rule in acceptance.Metrics)
            {
                bool hasBaseline = baselineMeasurement.Metrics.TryGetValue(
                    rule.Name,
                    out double baselineValue);
                bool hasCandidate = candidateMeasurement.Metrics.TryGetValue(
                    rule.Name,
                    out double candidateValue);
                if (!hasBaseline || !hasCandidate || !double.IsFinite(baselineValue) ||
                    !double.IsFinite(candidateValue))
                {
                    if (rule.Required)
                    {
                        reasons.Add(
                            $"{baselineMeasurement.ScenarioName}: required metric '{rule.Name}' is missing.");
                    }
                    continue;
                }

                double improvement = CalculateImprovementPercent(
                    baselineValue,
                    candidateValue,
                    rule.LowerIsBetter);
                bool regression = improvement < -rule.MaximumRegressionPercent;
                bool material = improvement >= rule.MinimumImprovementPercent;
                if (regression)
                {
                    reasons.Add(
                        $"{baselineMeasurement.ScenarioName}: {rule.Name} regressed {-improvement:F2}% " +
                        $"(allowed {rule.MaximumRegressionPercent:F2}%).");
                }
                if (rule.MaximumCandidateValue is double maximum &&
                    candidateValue > maximum)
                {
                    reasons.Add(
                        $"{baselineMeasurement.ScenarioName}: {rule.Name}={candidateValue:F3} exceeds {maximum:F3}.");
                }

                comparisons.Add(new SelfIterationMetricComparison
                {
                    Scenario = baselineMeasurement.ScenarioName,
                    Metric = rule.Name,
                    Baseline = baselineValue,
                    Candidate = candidateValue,
                    ImprovementPercent = improvement,
                    MaterialImprovement = material,
                    Regression = regression,
                });
                weightedImprovement += improvement * rule.Weight;
                totalWeight += rule.Weight;
                anyMaterialImprovement |= material;
            }
        }

        double aggregate = totalWeight > 0.0 ? weightedImprovement / totalWeight : 0.0;
        if (acceptance.RequireAnyMaterialImprovement && !anyMaterialImprovement)
            reasons.Add("No configured metric improved by its material-improvement threshold.");
        if (aggregate < acceptance.MinimumAggregateImprovementPercent)
        {
            reasons.Add(
                $"Weighted aggregate improvement {aggregate:F2}% is below " +
                $"{acceptance.MinimumAggregateImprovementPercent:F2}%.");
        }

        return new SelfIterationComparisonResult
        {
            Accepted = reasons.Count == 0,
            AggregateImprovementPercent = aggregate,
            Reasons = reasons,
            Metrics = comparisons,
        };
    }

    private static double CalculateImprovementPercent(
        double baseline,
        double candidate,
        bool lowerIsBetter)
    {
        double denominator = Math.Abs(baseline);
        if (denominator < 1e-9)
            return Math.Abs(candidate) < 1e-9 ? 0.0 : (lowerIsBetter ? -100.0 : 100.0);
        double delta = lowerIsBetter ? baseline - candidate : candidate - baseline;
        return delta / denominator * 100.0;
    }
}
