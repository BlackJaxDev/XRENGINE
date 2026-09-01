using System.Numerics;

namespace XREngine.Components.Animation
{
    public static class HumanoidPoseAuditComparer
    {
        private sealed class MetricAccumulator
        {
            public int Count;
            public float Sum;
            public float Max;
            public HumanoidPoseAuditWorstSample? WorstSample;

            public void Add(float value, HumanoidPoseAuditSample referenceSample, HumanoidPoseAuditSample actualSample)
            {
                Count++;
                Sum += value;
                if (Count > 1 && value <= Max)
                    return;

                Max = value;
                WorstSample = new HumanoidPoseAuditWorstSample
                {
                    ReferenceIndex = referenceSample.Index,
                    ReferenceTimeSeconds = referenceSample.TimeSeconds,
                    ActualIndex = actualSample.Index,
                    ActualTimeSeconds = actualSample.TimeSeconds,
                };
            }

            public HumanoidPoseAuditMetric ToMetric()
                => new()
                {
                    Count = Count,
                    Average = Count > 0 ? Sum / Count : 0.0f,
                    Max = Max,
                    WorstSample = WorstSample,
                };
        }

        public static HumanoidPoseAuditComparisonReport Compare(
            HumanoidPoseAuditReport reference,
            HumanoidPoseAuditReport actual,
            string? referencePath = null,
            string? actualPath = null)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentNullException.ThrowIfNull(actual);

            var report = new HumanoidPoseAuditComparisonReport
            {
                ReferencePath = referencePath,
                ActualPath = actualPath,
            };

            if (reference.SampleRate != actual.SampleRate)
                report.Warnings.Add($"Sample rate mismatch: reference={reference.SampleRate}, actual={actual.SampleRate}.");
            if (reference.Samples.Count != actual.Samples.Count)
                report.Warnings.Add($"Sample count mismatch: reference={reference.Samples.Count}, actual={actual.Samples.Count}.");

            if (reference.SchemaVersion < HumanoidPoseAuditReport.CurrentSchemaVersion
                || actual.SchemaVersion < HumanoidPoseAuditReport.CurrentSchemaVersion)
            {
                AddFailure(report, "SchemaVersion", "Schema-7 common-space fields are required for a conformance comparison.", null);
                return report;
            }

            ValidateDeclaredCoverage(reference, "reference", report);
            ValidateDeclaredCoverage(actual, "actual", report);
            if (report.Failures.Count > 0)
                return report;

            var bodyPosition = new MetricAccumulator();
            var bodyRotation = new MetricAccumulator();
            var projectedRootPosition = new MetricAccumulator();
            var projectedRootRotation = new MetricAccumulator();
            var temporalRootTranslation = new MetricAccumulator();
            var temporalRootRotation = new MetricAccumulator();
            var composedHipsPosition = new MetricAccumulator();
            var composedHipsRotation = new MetricAccumulator();
            var solvedBodyPosition = new MetricAccumulator();
            var solvedBodyRotation = new MetricAccumulator();
            var hipsModelRootPosition = new MetricAccumulator();
            var hipsModelRootRotation = new MetricAccumulator();
            var hipsWorldPosition = new MetricAccumulator();
            var hipsWorldRotation = new MetricAccumulator();
            var muscles = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var boneLocalPositions = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var boneRotations = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var boneRootSpacePositions = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var boneModelRootPositions = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var boneWorldRotations = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            bool convertSourceReferenceToXre = IsSourceToXreComparison(reference, actual);
            float referencePositionScale = convertSourceReferenceToXre
                ? ResolveEngineUnitsPerSourceMeter(actual)
                : 1.0f;
            if (convertSourceReferenceToXre)
            {
                report.Warnings.Add(
                    "Raw BodyPosition/BodyRotation metrics are omitted for Unity-to-XRENGINE comparisons because " +
                    "Unity HumanPose body values and importer-mapped RootT/RootQ are different diagnostic layers.");
            }

            bool timeMismatchLogged = false;
            bool missingTimeMatchLogged = false;
            int actualIndex = 0;
            int comparedSamples = 0;
            for (int i = 0; i < reference.Samples.Count; i++)
            {
                HumanoidPoseAuditSample referenceSample = reference.Samples[i];
                if (!TryFindClosestSampleAtTime(actual.Samples, referenceSample.TimeSeconds, ref actualIndex, out HumanoidPoseAuditSample actualSample))
                    break;

                float timeDifference = Math.Abs(referenceSample.TimeSeconds - actualSample.TimeSeconds);
                float timeTolerance = GetTimeAlignmentTolerance(reference, actual);
                if (timeDifference > timeTolerance)
                {
                    if (!missingTimeMatchLogged)
                    {
                        missingTimeMatchLogged = true;
                        report.Warnings.Add($"Unable to time-align samples within tolerance {timeTolerance:F6}s. First mismatch at reference index {i}: reference={referenceSample.TimeSeconds:F6}, actual={actualSample.TimeSeconds:F6}.");
                    }

                    continue;
                }

                comparedSamples++;

                if (!timeMismatchLogged && timeDifference > 0.0001f)
                {
                    timeMismatchLogged = true;
                    report.Warnings.Add($"Sample time mismatch at index {i}: reference={referenceSample.TimeSeconds:F6}, actual={actualSample.TimeSeconds:F6}.");
                }

                solvedBodyPosition.Add(Vector3.Distance(
                    referenceSample.SolvedBodyModelRootPositionMeters.Value,
                    actualSample.SolvedBodyModelRootPositionMeters.Value), referenceSample, actualSample);
                solvedBodyRotation.Add(QuaternionAngleDegrees(
                    referenceSample.SolvedBodyModelRootRotation.Value,
                    actualSample.SolvedBodyModelRootRotation.Value), referenceSample, actualSample);
                AccumulateCommonHipsErrors(referenceSample, actualSample,
                    hipsModelRootPosition, hipsModelRootRotation, hipsWorldPosition, hipsWorldRotation);

                if (!convertSourceReferenceToXre)
                {
                    bodyPosition.Add(
                        Vector3.Distance(referenceSample.BodyPosition.Value, actualSample.BodyPosition.Value),
                        referenceSample,
                        actualSample);
                    bodyRotation.Add(
                        QuaternionAngleDegrees(referenceSample.BodyRotation.Value, actualSample.BodyRotation.Value),
                        referenceSample,
                        actualSample);
                }

                projectedRootPosition.Add(
                    Vector3.Distance(
                        ConvertReferencePosition(
                            referenceSample.ProjectedRootPosition.Value,
                            convertSourceReferenceToXre,
                            referencePositionScale),
                        actualSample.ProjectedRootPosition.Value),
                    referenceSample,
                    actualSample);
                projectedRootRotation.Add(
                    QuaternionAngleDegrees(
                        ConvertReferenceRotation(referenceSample.ProjectedRootRotation.Value, convertSourceReferenceToXre),
                        actualSample.ProjectedRootRotation.Value),
                    referenceSample,
                    actualSample);

                AccumulateTemporalRootErrors(
                    referenceSample,
                    actualSample,
                    convertSourceReferenceToXre,
                    referencePositionScale,
                    temporalRootTranslation,
                    temporalRootRotation);

                AccumulateComposedHipsErrors(
                    referenceSample,
                    actualSample,
                    convertSourceReferenceToXre,
                    referencePositionScale,
                    composedHipsPosition,
                    composedHipsRotation);
                AccumulateNamedFloatErrors(referenceSample, actualSample, muscles);
                AccumulateBoneErrors(
                    referenceSample,
                    actualSample,
                    boneLocalPositions,
                    boneRotations,
                    boneRootSpacePositions,
                    boneModelRootPositions,
                    boneWorldRotations,
                    convertSourceReferenceToXre,
                    referencePositionScale);
            }

            report.ComparedSamples = comparedSamples;

            report.BodyPositionError = bodyPosition.ToMetric();
            report.BodyRotationErrorDegrees = bodyRotation.ToMetric();
            report.ProjectedRootPositionError = projectedRootPosition.ToMetric();
            report.ProjectedRootRotationErrorDegrees = projectedRootRotation.ToMetric();
            report.TemporalRootMotionTranslationError = temporalRootTranslation.ToMetric();
            report.TemporalRootMotionRotationErrorDegrees = temporalRootRotation.ToMetric();
            report.ComposedHipsLocalPositionError = composedHipsPosition.ToMetric();
            report.ComposedHipsLocalRotationErrorDegrees = composedHipsRotation.ToMetric();
            report.SolvedBodyModelRootPositionErrorMeters = solvedBodyPosition.ToMetric();
            report.SolvedBodyModelRootRotationErrorDegrees = solvedBodyRotation.ToMetric();
            report.HipsModelRootPositionErrorMeters = hipsModelRootPosition.ToMetric();
            report.HipsModelRootRotationErrorDegrees = hipsModelRootRotation.ToMetric();
            report.HipsWorldPositionErrorMeters = hipsWorldPosition.ToMetric();
            report.HipsWorldRotationErrorDegrees = hipsWorldRotation.ToMetric();
            report.MuscleAbsoluteError = ToMetricEntries(muscles);
            report.BoneLocalPositionError = ToMetricEntries(boneLocalPositions);
            report.BoneLocalRotationErrorDegrees = ToMetricEntries(boneRotations);
            report.BoneRootSpacePositionError = ToMetricEntries(boneRootSpacePositions);
            report.BoneModelRootPositionErrorMeters = ToMetricEntries(boneModelRootPositions);
            report.BoneWorldRotationErrorDegrees = ToMetricEntries(boneWorldRotations);
            return report;
        }

        private static void AccumulateCommonHipsErrors(
            HumanoidPoseAuditSample referenceSample, HumanoidPoseAuditSample actualSample,
            MetricAccumulator modelPosition, MetricAccumulator modelRotation,
            MetricAccumulator worldPosition, MetricAccumulator worldRotation)
        {
            modelPosition.Add(Vector3.Distance(
                referenceSample.HipsModelRootPositionMeters!.Value,
                actualSample.HipsModelRootPositionMeters!.Value), referenceSample, actualSample);
            modelRotation.Add(QuaternionAngleDegrees(
                referenceSample.HipsModelRootRotation!.Value,
                actualSample.HipsModelRootRotation!.Value), referenceSample, actualSample);
            worldPosition.Add(Vector3.Distance(
                referenceSample.HipsWorldPositionMeters!.Value,
                actualSample.HipsWorldPositionMeters!.Value), referenceSample, actualSample);
            worldRotation.Add(QuaternionAngleDegrees(
                referenceSample.HipsWorldRotation!.Value,
                actualSample.HipsWorldRotation!.Value), referenceSample, actualSample);
        }

        private static void AccumulateComposedHipsErrors(
            HumanoidPoseAuditSample referenceSample,
            HumanoidPoseAuditSample actualSample,
            bool convertSourceReferenceToXre,
            float referencePositionScale,
            MetricAccumulator positionAccumulator,
            MetricAccumulator rotationAccumulator)
        {
            HumanoidPoseAuditVector3? referencePosition = referenceSample.ComposedHipsLocalPosition
                ?? referenceSample.HipsLocalPosition
                ?? FindBone(referenceSample, "Hips")?.LocalPosition;
            HumanoidPoseAuditVector3? actualPosition = actualSample.ComposedHipsLocalPosition
                ?? actualSample.HipsLocalPosition
                ?? FindBone(actualSample, "Hips")?.LocalPosition;
            if (referencePosition is not null && actualPosition is not null)
            {
                positionAccumulator.Add(
                    Vector3.Distance(
                        ConvertReferencePosition(
                            referencePosition.Value,
                            convertSourceReferenceToXre,
                            referencePositionScale),
                        actualPosition.Value),
                    referenceSample,
                    actualSample);
            }

            HumanoidPoseAuditQuaternion? referenceRotation = referenceSample.ComposedHipsLocalRotation
                ?? referenceSample.HipsLocalRotation
                ?? FindBone(referenceSample, "Hips")?.LocalRotation;
            HumanoidPoseAuditQuaternion? actualRotation = actualSample.ComposedHipsLocalRotation
                ?? actualSample.HipsLocalRotation
                ?? FindBone(actualSample, "Hips")?.LocalRotation;
            if (referenceRotation is not null && actualRotation is not null)
            {
                rotationAccumulator.Add(
                    QuaternionAngleDegrees(
                        ConvertReferenceRotation(referenceRotation.Value, convertSourceReferenceToXre),
                        actualRotation.Value),
                    referenceSample,
                    actualSample);
            }
        }

        private static void AccumulateTemporalRootErrors(
            HumanoidPoseAuditSample referenceSample,
            HumanoidPoseAuditSample actualSample,
            bool convertSourceReferenceToXre,
            float referencePositionScale,
            MetricAccumulator translationAccumulator,
            MetricAccumulator rotationAccumulator)
        {
            HumanoidPoseAuditVector3? referenceTranslation = referenceSample.RootMotionDeltaPosition;
            HumanoidPoseAuditQuaternion? referenceRotation = referenceSample.RootMotionDeltaRotation;
            if (!convertSourceReferenceToXre)
            {
                referenceTranslation ??= referenceSample.TemporalRootMotionTranslation;
                referenceRotation ??= referenceSample.TemporalRootMotionRotation;
            }

            if (referenceTranslation is not null)
            {
                translationAccumulator.Add(
                    Vector3.Distance(
                        ConvertReferencePosition(
                            referenceTranslation.Value,
                            convertSourceReferenceToXre,
                            referencePositionScale),
                        actualSample.TemporalRootMotionTranslation.Value),
                    referenceSample,
                    actualSample);
            }

            if (referenceRotation is not null)
            {
                rotationAccumulator.Add(
                    QuaternionAngleDegrees(
                        ConvertReferenceRotation(referenceRotation.Value, convertSourceReferenceToXre),
                        actualSample.TemporalRootMotionRotation.Value),
                    referenceSample,
                    actualSample);
            }
        }

        private static bool TryFindClosestSampleAtTime(
            IReadOnlyList<HumanoidPoseAuditSample> samples,
            float targetTime,
            ref int startIndex,
            out HumanoidPoseAuditSample sample)
        {
            sample = null!;
            if (samples.Count == 0)
                return false;

            startIndex = Math.Clamp(startIndex, 0, samples.Count - 1);
            while (startIndex + 1 < samples.Count)
            {
                float currentDelta = Math.Abs(samples[startIndex].TimeSeconds - targetTime);
                float nextDelta = Math.Abs(samples[startIndex + 1].TimeSeconds - targetTime);
                if (nextDelta > currentDelta)
                    break;

                startIndex++;
            }

            sample = samples[startIndex];
            return true;
        }

        private static float GetTimeAlignmentTolerance(HumanoidPoseAuditReport reference, HumanoidPoseAuditReport actual)
        {
            float referenceStep = GetNominalSampleStep(reference);
            float actualStep = GetNominalSampleStep(actual);
            return Math.Max(0.0001f, 0.5f * Math.Max(referenceStep, actualStep));
        }

        private static float GetNominalSampleStep(HumanoidPoseAuditReport report)
        {
            if (report.SampleRate > 0)
                return 1.0f / report.SampleRate;

            if (report.Samples.Count > 1)
                return Math.Max(0.0001f, report.Samples[1].TimeSeconds - report.Samples[0].TimeSeconds);

            if (report.DurationSeconds > 0.0f && report.SampleCount > 1)
                return report.DurationSeconds / (report.SampleCount - 1);

            return 1.0f / 30.0f;
        }

        private static void AccumulateNamedFloatErrors(
            HumanoidPoseAuditSample referenceSample,
            HumanoidPoseAuditSample actualSample,
            Dictionary<string, MetricAccumulator> accumulators)
        {
            var actualByName = ToCanonicalNamedFloatDictionary(actualSample.Muscles);
            foreach (var entry in referenceSample.Muscles)
            {
                string canonicalName = CanonicalizeMuscleName(entry.Name);
                if (!actualByName.TryGetValue(canonicalName, out float actualValue))
                    continue;

                GetOrAdd(accumulators, canonicalName).Add(
                    Math.Abs(entry.Value - actualValue),
                    referenceSample,
                    actualSample);
            }
        }

        private static void AccumulateBoneErrors(
            HumanoidPoseAuditSample referenceSample,
            HumanoidPoseAuditSample actualSample,
            Dictionary<string, MetricAccumulator> localPositionAccumulators,
            Dictionary<string, MetricAccumulator> rotationAccumulators,
            Dictionary<string, MetricAccumulator> rootSpacePositionAccumulators,
            Dictionary<string, MetricAccumulator> modelRootPositionAccumulators,
            Dictionary<string, MetricAccumulator> worldRotationAccumulators,
            bool convertSourceReferenceToXre,
            float referencePositionScale)
        {
            var actualByName = actualSample.Bones.ToDictionary(static x => x.Name, StringComparer.Ordinal);
            foreach (var entry in referenceSample.Bones)
            {
                if (!actualByName.TryGetValue(entry.Name, out var actualBone))
                    continue;

                GetOrAdd(localPositionAccumulators, entry.Name)
                    .Add(
                        Vector3.Distance(
                            ConvertReferencePosition(
                                entry.LocalPosition.Value,
                                convertSourceReferenceToXre,
                                referencePositionScale),
                            actualBone.LocalPosition.Value),
                        referenceSample,
                        actualSample);
                GetOrAdd(rotationAccumulators, entry.Name)
                    .Add(
                        QuaternionAngleDegrees(
                            ConvertReferenceRotation(entry.PoseDeltaFromNeutralRotation.Value, convertSourceReferenceToXre),
                            actualBone.PoseDeltaFromNeutralRotation.Value),
                        referenceSample,
                        actualSample);
                GetOrAdd(rootSpacePositionAccumulators, entry.Name)
                    .Add(
                        Vector3.Distance(
                            ConvertReferencePosition(
                                entry.RootSpacePosition.Value,
                                convertSourceReferenceToXre,
                                referencePositionScale),
                            actualBone.RootSpacePosition.Value),
                        referenceSample,
                        actualSample);
                GetOrAdd(modelRootPositionAccumulators, entry.Name)
                    .Add(Vector3.Distance(entry.ModelRootPositionMeters.Value, actualBone.ModelRootPositionMeters.Value),
                        referenceSample, actualSample);
                GetOrAdd(worldRotationAccumulators, entry.Name)
                    .Add(QuaternionAngleDegrees(entry.WorldRotation.Value, actualBone.WorldRotation.Value),
                        referenceSample, actualSample);
            }
        }

        private static void ValidateDeclaredCoverage(
            HumanoidPoseAuditReport reportToValidate,
            string side,
            HumanoidPoseAuditComparisonReport comparison)
        {
            if (reportToValidate.RequiredBoneRoles.Count == 0)
                AddFailure(comparison, "RequiredBoneRolesMissing", $"The {side} report declares no required bone roles.", null);
            if (reportToValidate.RequiredMuscleChannels.Count == 0)
                AddFailure(comparison, "RequiredMuscleChannelsMissing", $"The {side} report declares no required muscle channels.", null);

            ValidateNoDuplicates(reportToValidate.RequiredBoneRoles, $"{side}:RequiredBoneRoles", comparison, null);
            ValidateNoDuplicates(reportToValidate.RequiredMuscleChannels, $"{side}:RequiredMuscleChannels", comparison, null);
            for (int i = 0; i < reportToValidate.Samples.Count; i++)
            {
                HumanoidPoseAuditSample sample = reportToValidate.Samples[i];
                ValidateSampleCoverage(reportToValidate.RequiredBoneRoles, sample.Bones.Select(static bone => bone.Name),
                    $"{side}:BoneRole", comparison, sample.Index);
                ValidateSampleCoverage(reportToValidate.RequiredMuscleChannels, sample.Muscles.Select(static channel => channel.Name),
                    $"{side}:MuscleChannel", comparison, sample.Index);
                if (!sample.HasSolvedBodyModelRootPose)
                    AddFailure(comparison, "SolvedBodyCommonPoseMissing", $"The {side} sample has no solved Body model-root pose.", sample.Index);
                if (sample.HipsModelRootPositionMeters is null || sample.HipsModelRootRotation is null
                    || sample.HipsWorldPositionMeters is null || sample.HipsWorldRotation is null)
                    AddFailure(comparison, "HipsCommonPoseMissing", $"The {side} sample is missing one or more required schema-7 Hips poses.", sample.Index);
            }
        }

        private static void ValidateSampleCoverage(
            IReadOnlyList<string> required, IEnumerable<string> actual, string category,
            HumanoidPoseAuditComparisonReport comparison, int sampleIndex)
        {
            var actualCounts = actual.GroupBy(static value => value, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
            foreach (string name in required)
            {
                if (!actualCounts.TryGetValue(name, out int count))
                    AddFailure(comparison, category + "Missing", $"Required '{name}' is absent.", sampleIndex);
                else if (count != 1)
                    AddFailure(comparison, category + "Duplicate", $"Required '{name}' occurs {count} times.", sampleIndex);
            }
        }

        private static void ValidateNoDuplicates(
            IReadOnlyList<string> values, string category, HumanoidPoseAuditComparisonReport comparison, int? sampleIndex)
        {
            foreach (IGrouping<string, string> group in values.GroupBy(static value => value, StringComparer.Ordinal))
                if (group.Count() != 1)
                    AddFailure(comparison, category + "Duplicate", $"Declared '{group.Key}' occurs {group.Count()} times.", sampleIndex);
        }

        private static void AddFailure(HumanoidPoseAuditComparisonReport report, string code, string message, int? sampleIndex)
            => report.Failures.Add(new HumanoidPoseAuditComparisonFailure
            {
                Code = code,
                Message = message,
                SampleIndex = sampleIndex,
            });

        private static HumanoidPoseAuditBoneSample? FindBone(HumanoidPoseAuditSample sample, string name)
        {
            for (int i = 0; i < sample.Bones.Count; i++)
                if (string.Equals(sample.Bones[i].Name, name, StringComparison.Ordinal))
                    return sample.Bones[i];
            return null;
        }

        private static bool IsSourceToXreComparison(
            HumanoidPoseAuditReport reference,
            HumanoidPoseAuditReport actual)
            => reference.Source.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
            && actual.Source.StartsWith("XREngine", StringComparison.OrdinalIgnoreCase);

        private static float ResolveEngineUnitsPerSourceMeter(HumanoidPoseAuditReport report)
            => float.IsFinite(report.EngineUnitsPerSourceMeter) && report.EngineUnitsPerSourceMeter > 0.0f
                ? report.EngineUnitsPerSourceMeter
                : 39.370064f;

        private static Vector3 ConvertReferencePosition(
            Vector3 value,
            bool convertSourceReferenceToXre,
            float scale)
            => convertSourceReferenceToXre
                ? new Vector3(-value.X, value.Y, value.Z) * scale
                : value;

        private static Quaternion ConvertReferenceRotation(Quaternion value, bool convertSourceReferenceToXre)
            => convertSourceReferenceToXre
                ? Quaternion.Normalize(new Quaternion(value.X, -value.Y, -value.Z, value.W))
                : value;

        private static List<HumanoidPoseAuditMetricEntry> ToMetricEntries(Dictionary<string, MetricAccumulator> accumulators)
            => accumulators
                .Select(static kvp => new HumanoidPoseAuditMetricEntry
                {
                    Name = kvp.Key,
                    Metric = kvp.Value.ToMetric(),
                })
                .OrderByDescending(static x => x.Metric.Max)
                .ThenBy(static x => x.Name, StringComparer.Ordinal)
                .ToList();

        private static MetricAccumulator GetOrAdd(Dictionary<string, MetricAccumulator> accumulators, string name)
        {
            if (!accumulators.TryGetValue(name, out var accumulator))
            {
                accumulator = new MetricAccumulator();
                accumulators.Add(name, accumulator);
            }

            return accumulator;
        }

        private static float QuaternionAngleDegrees(Quaternion a, Quaternion b)
        {
            a = Quaternion.Normalize(a);
            b = Quaternion.Normalize(b);

            float dot = Math.Abs(Quaternion.Dot(a, b));
            dot = Math.Clamp(dot, -1.0f, 1.0f);
            return MathF.Acos(dot) * 2.0f * (180.0f / MathF.PI);
        }

        private static Dictionary<string, float> ToCanonicalNamedFloatDictionary(IReadOnlyList<HumanoidPoseAuditNamedFloat> entries)
        {
            var map = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var entry in entries)
                map[CanonicalizeMuscleName(entry.Name)] = entry.Value;

            return map;
        }

        private static string CanonicalizeMuscleName(string name)
        {
            if (!ImportedHumanoidMuscleMap.TryGetValue(name, out var value))
                return name;

            return ImportedHumanoidMuscleMap.TryGetHumanTraitName(value, out string humanTraitName)
                ? humanTraitName
                : value.ToString();
        }
    }
}
