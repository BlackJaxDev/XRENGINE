namespace XREngine.Components.Animation;

/// <summary>Calculates Phase 10 coverage strictly from completed asset and playback observations.</summary>
public static class HumanoidConformanceCoverageEvaluator
{
    /// <summary>
    /// Evaluates declared requirements from runner-produced observations. Manifest
    /// expectation masks never satisfy coverage by themselves.
    /// </summary>
    public static HumanoidConformanceCoverageEvaluationResult Evaluate(
        HumanoidConformanceManifest manifest,
        IEnumerable<HumanoidConformanceAssetCheckResult> assetResults,
        IEnumerable<HumanoidConformanceMatrixCheckResult> matrixResults)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assetResults);
        ArgumentNullException.ThrowIfNull(matrixResults);

        var result = new HumanoidConformanceCoverageEvaluationResult
        {
            RequiredCoverage = manifest.RequiredCoverage,
        };
        var declaredAssetChecks = manifest.AssetChecks.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        var declaredMatrixCases = manifest.Matrix.ToDictionary(static x => x.Id, StringComparer.Ordinal);

        foreach (HumanoidConformanceAssetCheckResult observation in assetResults)
        {
            if (!declaredAssetChecks.TryGetValue(observation.AssetCheckId, out HumanoidConformanceAssetCheck? declared))
            {
                result.Failures.Add($"Unknown asset check observation '{observation.AssetCheckId}'.");
                continue;
            }
            if (observation.Passed != declared.ExpectedToPass)
            {
                result.Failures.Add($"Asset check '{observation.AssetCheckId}' expected pass={declared.ExpectedToPass}, observed pass={observation.Passed}.");
                continue;
            }
            if (observation.Passed)
            {
                HumanoidConformanceCapability missingExpected = declared.ExpectedCapabilities & ~observation.ObservedCapabilities;
                if (missingExpected != HumanoidConformanceCapability.None)
                    result.Failures.Add($"Asset check '{observation.AssetCheckId}' did not observe expected capabilities: {missingExpected}.");
                result.ObservedCoverage |= observation.ObservedCapabilities;
            }
        }

        foreach (HumanoidConformanceMatrixCheckResult observation in matrixResults)
        {
            if (!declaredMatrixCases.TryGetValue(observation.MatrixCaseId, out HumanoidConformanceMatrixCase? declared))
            {
                result.Failures.Add($"Unknown matrix observation '{observation.MatrixCaseId}'.");
                continue;
            }
            if (!observation.Passed)
            {
                result.Failures.Add($"Matrix case '{observation.MatrixCaseId}' did not pass: {observation.Diagnostic}");
                continue;
            }
            HumanoidConformanceCapability missingExpected = declared.ExpectedCapabilities & ~observation.ObservedCapabilities;
            if (missingExpected != HumanoidConformanceCapability.None)
                result.Failures.Add($"Matrix case '{observation.MatrixCaseId}' did not observe expected capabilities: {missingExpected}.");
            result.ObservedCoverage |= observation.ObservedCapabilities;
        }

        result.MissingCoverage = result.RequiredCoverage & ~result.ObservedCoverage;
        return result;
    }
}
