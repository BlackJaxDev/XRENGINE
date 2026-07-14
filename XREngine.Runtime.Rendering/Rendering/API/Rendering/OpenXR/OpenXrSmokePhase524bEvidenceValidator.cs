namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Behavioral validation shared by the smoke controller and unit tests. This
/// deliberately validates values and output dimensions rather than source text.
/// </summary>
public static class OpenXrSmokePhase524bEvidenceValidator
{
    private static readonly string[] s_temporalScenarioStages =
    [
        "07_Velocity",
        "09_BloomMip1",
        "13c_MonoTsrReference",
        "14_TsrOutput",
    ];

    public static string[] ValidateDesktopRejection(
        OpenXrSmokeDesktopRejectionEvidence? evidence,
        bool required)
    {
        if (!required)
            return [];

        var failures = new List<string>();
        if (evidence is null || !evidence.Injected || !evidence.Observed)
        {
            failures.Add("The controlled desktop rejection was not injected and observed.");
            return [.. failures];
        }

        if (evidence.ClearedTargetPublished)
            failures.Add("The controlled desktop rejection published a cleared target.");
        if (evidence.SkippedPresent == evidence.PresentedLastCompletedImage)
            failures.Add("The controlled desktop rejection did not select exactly one legal presentation policy.");
        if (evidence.PresentedLastCompletedImage && !evidence.PresentAccepted)
            failures.Add("The last-completed-image presentation was not accepted.");
        if (!evidence.ExposureFinite || !double.IsFinite(evidence.Exposure))
            failures.Add("Desktop exposure was not finite.");
        if (!evidence.ExposureHistoryFinite || !double.IsFinite(evidence.ExposureHistory))
            failures.Add("Desktop exposure history was not finite.");
        if (evidence.ExposureNonZeroRequired && evidence.Exposure <= 0.0)
            failures.Add("Desktop exposure was not positive for the HDR validation scene.");
        if (evidence.ExposureHistoryNonZeroRequired && evidence.ExposureHistory <= 0.0)
            failures.Add("Desktop exposure history was not positive for the HDR validation scene.");
        if (!evidence.ExposureOwnerMatchesDesktop ||
            evidence.PipelineInstanceId <= 0 ||
            evidence.OutputId == 0UL ||
            string.IsNullOrWhiteSpace(evidence.PipelineName))
        {
            failures.Add("Exposure samples were not tied to a concrete desktop pipeline/output owner.");
        }
        if (evidence.RenderFrameId == 0UL)
            failures.Add("The controlled desktop rejection did not record its render frame identity.");

        return [.. failures];
    }

    public static string[] ValidateTsrResolutionCohort(
        double requestedScale,
        double effectiveScale,
        IReadOnlyList<OpenXrSmokeOutputLedgerEntry>? outputs,
        bool expectSubNative)
    {
        var failures = new List<string>();
        if (!double.IsFinite(requestedScale) || !double.IsFinite(effectiveScale) ||
            requestedScale < 0.5 || requestedScale > 1.0 ||
            Math.Abs(requestedScale - effectiveScale) > 0.0001)
        {
            failures.Add($"TSR resolution scale mismatch: requested={requestedScale}, effective={effectiveScale}.");
        }

        OpenXrSmokeOutputLedgerEntry[] strictSpsOutputs = outputs is null
            ? []
            : [.. outputs.Where(static output =>
                output.TargetClass == "RuntimeExternalImage" &&
                output.LayerCount == 2u &&
                output.ViewMask == 0x3u &&
                output.Rendered &&
                output.PipelineInstanceId > 0)];
        if (strictSpsOutputs.Length == 0)
        {
            failures.Add("The TSR cohort contained no rendered strict-SPS output samples.");
            return [.. failures];
        }

        for (int i = 0; i < strictSpsOutputs.Length; i++)
        {
            OpenXrSmokeOutputLedgerEntry output = strictSpsOutputs[i];
            bool subNative = output.InternalWidth < output.DisplayWidth &&
                output.InternalHeight < output.DisplayHeight;
            bool native = output.InternalWidth == output.DisplayWidth &&
                output.InternalHeight == output.DisplayHeight;
            if (expectSubNative ? !subNative : !native)
            {
                failures.Add(
                    $"TSR cohort output {output.OutputId} has display={output.DisplayWidth}x{output.DisplayHeight}, " +
                    $"internal={output.InternalWidth}x{output.InternalHeight}, expected " +
                    (expectSubNative ? "sub-native" : "native") + ".");
                continue;
            }

            if (expectSubNative)
            {
                double widthScale = (double)output.InternalWidth / output.DisplayWidth;
                double heightScale = (double)output.InternalHeight / output.DisplayHeight;
                double roundingTolerance = Math.Max(2.0 / output.DisplayWidth, 2.0 / output.DisplayHeight);
                if (Math.Abs(widthScale - effectiveScale) > roundingTolerance ||
                    Math.Abs(heightScale - effectiveScale) > roundingTolerance)
                {
                    failures.Add(
                        $"TSR cohort output {output.OutputId} resolved to {widthScale:F4}x{heightScale:F4}, " +
                        $"expected {effectiveScale:F4} within pixel-rounding tolerance.");
                }
            }
        }

        return [.. failures];
    }

    /// <summary>
    /// Validates the predeclared six-scenario live output matrix. The
    /// PreTsrHistoryColor capture is the controlled mono-equivalent source for
    /// each eye: a single current-frame input before TSR reconstruction. The
    /// paired TsrOutput is required to preserve its edge response and converge
    /// to it in settled scenarios.
    /// </summary>
    public static string[] ValidateTemporalScenarioMatrix(
        IReadOnlyList<OpenXrSmokeTemporalScenarioDefinition>? matrix,
        IReadOnlyList<string>? captureStages,
        IReadOnlyList<OpenXrSmokeCaptureLedgerEntry>? captures)
    {
        var failures = new List<string>();
        ReadOnlySpan<Phase524bTemporalSampleDefinition> expectedDefinitions =
            Phase524bTemporalScenarioDiagnostics.Definitions;
        if (matrix is null || matrix.Count != expectedDefinitions.Length)
        {
            failures.Add(
                $"Temporal scenario matrix contains {matrix?.Count ?? 0} definitions; " +
                $"expected exactly {expectedDefinitions.Length}.");
            return [.. failures];
        }

        if (captureStages is null ||
            captureStages.Count != s_temporalScenarioStages.Length ||
            !captureStages.SequenceEqual(s_temporalScenarioStages, StringComparer.Ordinal))
        {
            failures.Add(
                "Temporal scenario stages must be exactly: " +
                string.Join(", ", s_temporalScenarioStages) + ".");
        }

        var definitionsBySample = new Dictionary<string, OpenXrSmokeTemporalScenarioDefinition>(
            expectedDefinitions.Length,
            StringComparer.Ordinal);
        for (int i = 0; i < expectedDefinitions.Length; i++)
        {
            Phase524bTemporalSampleDefinition expected = expectedDefinitions[i];
            OpenXrSmokeTemporalScenarioDefinition? actual = matrix.FirstOrDefault(
                definition => definition.Sample == expected.Sample.ToString());
            if (actual is null)
            {
                failures.Add($"Temporal scenario definition '{expected.Sample}' is missing.");
                continue;
            }

            if (actual.Scenario != expected.Scenario.ToString() ||
                actual.VelocityOracle != expected.VelocityOracle.ToString() ||
                actual.CaptureStartFrame != expected.CaptureStartFrame ||
                actual.CaptureEndFrame != expected.CaptureEndFrame ||
                actual.RequiresTemporalConvergence != expected.RequiresTemporalConvergence ||
                actual.IsDisocclusionBaseline != expected.IsDisocclusionBaseline ||
                actual.IsDisocclusionResult != expected.IsDisocclusionResult)
            {
                failures.Add($"Temporal scenario definition '{expected.Sample}' does not match its predeclared oracle.");
            }
            if (!definitionsBySample.TryAdd(actual.Sample, actual))
                failures.Add($"Temporal scenario definition '{actual.Sample}' appears more than once.");
        }

        int expectedCaptureCount = expectedDefinitions.Length * s_temporalScenarioStages.Length * 2;
        if (captures is null || captures.Count != expectedCaptureCount)
        {
            failures.Add(
                $"Temporal scenario capture ledger contains {captures?.Count ?? 0} entries; " +
                $"expected exactly {expectedCaptureCount}.");
        }
        if (captures is null)
            return [.. failures];

        var capturesByKey = new Dictionary<(string Sample, string Stage, int Layer), OpenXrSmokeCaptureLedgerEntry>(
            expectedCaptureCount);
        for (int i = 0; i < captures.Count; i++)
        {
            OpenXrSmokeCaptureLedgerEntry capture = captures[i];
            var key = (capture.TemporalSample, capture.Stage, capture.LayerIndex);
            if (!capturesByKey.TryAdd(key, capture))
            {
                failures.Add(
                    $"Temporal capture '{capture.TemporalSample}/{capture.Stage}/layer{capture.LayerIndex}' appears more than once.");
                continue;
            }

            if (!definitionsBySample.TryGetValue(capture.TemporalSample, out OpenXrSmokeTemporalScenarioDefinition? definition))
            {
                failures.Add($"Temporal capture references unknown sample '{capture.TemporalSample}'.");
                continue;
            }

            bool shapeValid = capture.PipelineName == "DefaultPipelineSps" &&
                capture.OutputRole == "TemporalScenarioRenderedOutput" &&
                capture.ExpectedLayerCount == 2 &&
                capture.ViewMask == 0x3u &&
                capture.LayerIndex is 0 or 1 &&
                Array.IndexOf(s_temporalScenarioStages, capture.Stage) >= 0 &&
                capture.TemporalScenario == definition.Scenario &&
                capture.VelocityOracle == definition.VelocityOracle &&
                capture.RenderFrameId != 0UL &&
                capture.TemporalSequenceFrame >= definition.CaptureStartFrame &&
                capture.TemporalSequenceFrame <= definition.CaptureEndFrame &&
                capture.Width > 0 && capture.Height > 0 &&
                capture.LengthBytes > 0 &&
                capture.LuminanceFingerprintWidth == 16 &&
                capture.LuminanceFingerprintHeight == 16 &&
                capture.LuminanceFingerprint.Length == 256 &&
                capture.VelocityMagnitudeFingerprintWidth == 16 &&
                capture.VelocityMagnitudeFingerprintHeight == 16 &&
                capture.VelocityMagnitudeFingerprint.Length == 256;
            if (!shapeValid)
            {
                failures.Add(
                    $"Temporal capture '{capture.TemporalSample}/{capture.Stage}/layer{capture.LayerIndex}' " +
                    "has invalid identity, frame, dimensions, or fingerprint evidence.");
            }
        }

        for (int definitionIndex = 0; definitionIndex < expectedDefinitions.Length; definitionIndex++)
        {
            Phase524bTemporalSampleDefinition definition = expectedDefinitions[definitionIndex];
            string sample = definition.Sample.ToString();
            var sampleCaptures = new OpenXrSmokeCaptureLedgerEntry[s_temporalScenarioStages.Length * 2];
            int sampleCaptureCount = 0;
            for (int stageIndex = 0; stageIndex < s_temporalScenarioStages.Length; stageIndex++)
            {
                string stage = s_temporalScenarioStages[stageIndex];
                for (int layerIndex = 0; layerIndex < 2; layerIndex++)
                {
                    if (!capturesByKey.TryGetValue((sample, stage, layerIndex), out OpenXrSmokeCaptureLedgerEntry? capture))
                    {
                        failures.Add($"Temporal capture '{sample}/{stage}/layer{layerIndex}' is missing.");
                        continue;
                    }
                    sampleCaptures[sampleCaptureCount++] = capture;
                }
            }

            if (sampleCaptureCount != sampleCaptures.Length)
                continue;
            if (sampleCaptures.Select(static capture => capture.RenderFrameId).Distinct().Count() != 1 ||
                sampleCaptures.Select(static capture => capture.TemporalSequenceFrame).Distinct().Count() != 1)
            {
                failures.Add($"Temporal sample '{sample}' was not captured from one rendered SPS frame.");
            }

            OpenXrSmokeCaptureLedgerEntry velocityLeft = capturesByKey[(sample, "07_Velocity", 0)];
            OpenXrSmokeCaptureLedgerEntry velocityRight = capturesByKey[(sample, "07_Velocity", 1)];
            ValidateVelocityOracle(definition, velocityLeft, velocityRight, failures);

            OpenXrSmokeCaptureLedgerEntry bloomLeft = capturesByKey[(sample, "09_BloomMip1", 0)];
            OpenXrSmokeCaptureLedgerEntry bloomRight = capturesByKey[(sample, "09_BloomMip1", 1)];
            ValidateBloomOracle(sample, bloomLeft, bloomRight, failures);

            for (int layerIndex = 0; layerIndex < 2; layerIndex++)
            {
                OpenXrSmokeCaptureLedgerEntry monoEquivalent =
                    capturesByKey[(sample, "13c_MonoTsrReference", layerIndex)];
                OpenXrSmokeCaptureLedgerEntry temporalOutput =
                    capturesByKey[(sample, "14_TsrOutput", layerIndex)];
                ValidateTemporalOutputOracle(
                    definition,
                    layerIndex,
                    monoEquivalent,
                    temporalOutput,
                    failures);
            }
        }

        ValidateDisocclusionOracle(capturesByKey, failures);
        return [.. failures];
    }

    private static void ValidateVelocityOracle(
        in Phase524bTemporalSampleDefinition definition,
        OpenXrSmokeCaptureLedgerEntry left,
        OpenXrSmokeCaptureLedgerEntry right,
        List<string> failures)
    {
        string sample = definition.Sample.ToString();
        if (definition.VelocityOracle == EPhase524bVelocityOracle.Zero)
        {
            if (left.VelocityMaxMagnitude > StereoRenderedOutputThresholds.MaxStaticVelocityMagnitude ||
                right.VelocityMaxMagnitude > StereoRenderedOutputThresholds.MaxStaticVelocityMagnitude)
            {
                failures.Add(
                    $"Temporal sample '{sample}' exceeded the static-zero velocity limit: " +
                    $"left={left.VelocityMaxMagnitude}, right={right.VelocityMaxMagnitude}.");
            }
            return;
        }

        if (left.VelocityMaxMagnitude < StereoRenderedOutputThresholds.MinMovingVelocityMagnitude ||
            right.VelocityMaxMagnitude < StereoRenderedOutputThresholds.MinMovingVelocityMagnitude)
        {
            failures.Add(
                $"Temporal sample '{sample}' did not produce moving velocity in both eyes: " +
                $"left={left.VelocityMaxMagnitude}, right={right.VelocityMaxMagnitude}.");
        }

        float direction = definition.VelocityOracle == EPhase524bVelocityOracle.PositiveX ? 1.0f : -1.0f;
        if (left.VelocityMeanX * direction <= StereoRenderedOutputThresholds.MinDirectionalVelocityComponent ||
            right.VelocityMeanX * direction <= StereoRenderedOutputThresholds.MinDirectionalVelocityComponent)
        {
            failures.Add(
                $"Temporal sample '{sample}' velocity direction did not match {definition.VelocityOracle}: " +
                $"leftX={left.VelocityMeanX}, rightX={right.VelocityMeanX}.");
        }

        double eyeRmse = StereoRenderedOutputMetrics.RootMeanSquareError(
            left.VelocityMagnitudeFingerprint,
            right.VelocityMagnitudeFingerprint);
        if (eyeRmse <= StereoRenderedOutputThresholds.MinStereoEyeSpecificVelocityRmse)
        {
            failures.Add(
                $"Temporal sample '{sample}' velocity evidence was not eye-specific (RMSE={eyeRmse}).");
        }
    }

    private static void ValidateBloomOracle(
        string sample,
        OpenXrSmokeCaptureLedgerEntry left,
        OpenXrSmokeCaptureLedgerEntry right,
        List<string> failures)
    {
        if (left.LuminanceEnergy <= double.Epsilon || right.LuminanceEnergy <= double.Epsilon)
        {
            failures.Add($"Temporal sample '{sample}' bloom energy was empty in one or both eyes.");
            return;
        }

        if (!IsBloomCentroidInBounds(left) || !IsBloomCentroidInBounds(right))
        {
            failures.Add(
                $"Temporal sample '{sample}' bloom centroid left the deterministic validation envelope: " +
                $"left=({left.BloomCentroidX},{left.BloomCentroidY}), " +
                $"right=({right.BloomCentroidX},{right.BloomCentroidY}).");
        }

        double energyDelta = StereoRenderedOutputMetrics.RelativeDifference(
            left.LuminanceEnergy,
            right.LuminanceEnergy);
        double centroidX = left.BloomCentroidX - right.BloomCentroidX;
        double centroidY = left.BloomCentroidY - right.BloomCentroidY;
        double centroidDistance = Math.Sqrt((centroidX * centroidX) + (centroidY * centroidY));
        if (energyDelta > StereoRenderedOutputThresholds.MaxBloomRelativeEnergyDelta ||
            centroidDistance > StereoRenderedOutputThresholds.MaxBloomCentroidDistance)
        {
            failures.Add(
                $"Temporal sample '{sample}' bloom diverged across eyes: " +
                $"energyDelta={energyDelta}, centroidDistance={centroidDistance}.");
        }

        double eyeRmse = StereoRenderedOutputMetrics.RootMeanSquareError(
            left.LuminanceFingerprint,
            right.LuminanceFingerprint);
        if (eyeRmse <= StereoRenderedOutputThresholds.MinStereoEyeSpecificVelocityRmse)
        {
            failures.Add(
                $"Temporal sample '{sample}' bloom layers were not independently rendered (RMSE={eyeRmse}).");
        }
    }

    private static bool IsBloomCentroidInBounds(OpenXrSmokeCaptureLedgerEntry capture)
    {
        float tolerance = StereoRenderedOutputThresholds.BloomCentroidBoundsTolerance;
        return float.IsFinite(capture.BloomCentroidX) &&
            float.IsFinite(capture.BloomCentroidY) &&
            capture.BloomCentroidX >= StereoRenderedOutputThresholds.MinBloomCentroidX - tolerance &&
            capture.BloomCentroidX <= StereoRenderedOutputThresholds.MaxBloomCentroidX + tolerance &&
            capture.BloomCentroidY >= StereoRenderedOutputThresholds.MinBloomCentroidY - tolerance &&
            capture.BloomCentroidY <= StereoRenderedOutputThresholds.MaxBloomCentroidY + tolerance;
    }

    private static void ValidateTemporalOutputOracle(
        in Phase524bTemporalSampleDefinition definition,
        int layerIndex,
        OpenXrSmokeCaptureLedgerEntry monoEquivalent,
        OpenXrSmokeCaptureLedgerEntry temporalOutput,
        List<string> failures)
    {
        string sample = definition.Sample.ToString();
        float minimumSharpnessRatio = definition.RequiresTemporalConvergence
            ? StereoRenderedOutputThresholds.MinStaticEdgeSharpnessRatioToMono
            : StereoRenderedOutputThresholds.MinMovingEdgeSharpnessRatioToMono;
        if (monoEquivalent.EdgeMaxGradient <= float.Epsilon ||
            temporalOutput.EdgeMaxGradient / monoEquivalent.EdgeMaxGradient < minimumSharpnessRatio)
        {
            failures.Add(
                $"Temporal sample '{sample}' layer {layerIndex} lost edge sharpness against its " +
                $"mono-equivalent input (required ratio={minimumSharpnessRatio}).");
        }

        if (!definition.RequiresTemporalConvergence)
            return;

        double rmse = StereoRenderedOutputMetrics.RootMeanSquareError(
            temporalOutput.LuminanceFingerprint,
            monoEquivalent.LuminanceFingerprint);
        if (rmse > StereoRenderedOutputThresholds.MaxTemporalConvergenceRmse)
        {
            failures.Add(
                $"Temporal sample '{sample}' layer {layerIndex} did not converge to its " +
                $"mono-equivalent input (RMSE={rmse}).");
        }
    }

    private static void ValidateDisocclusionOracle(
        IReadOnlyDictionary<(string Sample, string Stage, int Layer), OpenXrSmokeCaptureLedgerEntry> captures,
        List<string> failures)
    {
        const string occludedSample = nameof(EPhase524bTemporalSample.DisocclusionOccluded);
        const string revealedSample = nameof(EPhase524bTemporalSample.DisocclusionRevealed);
        for (int layerIndex = 0; layerIndex < 2; layerIndex++)
        {
            if (!captures.TryGetValue(
                    (occludedSample, "13c_MonoTsrReference", layerIndex),
                    out OpenXrSmokeCaptureLedgerEntry? occluded) ||
                !captures.TryGetValue(
                    (revealedSample, "13c_MonoTsrReference", layerIndex),
                    out OpenXrSmokeCaptureLedgerEntry? revealed))
            {
                continue;
            }

            double rmse = StereoRenderedOutputMetrics.RootMeanSquareError(
                occluded.LuminanceFingerprint,
                revealed.LuminanceFingerprint);
            if (rmse < StereoRenderedOutputThresholds.MinDisocclusionFingerprintRmse)
            {
                failures.Add(
                    $"Disocclusion layer {layerIndex} did not reveal a changed rendered input (RMSE={rmse}).");
            }
        }
    }
}
