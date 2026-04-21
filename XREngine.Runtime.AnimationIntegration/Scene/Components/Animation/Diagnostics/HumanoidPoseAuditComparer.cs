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

            public void Add(float value)
            {
                Count++;
                Sum += value;
                Max = Math.Max(Max, value);
            }

            public HumanoidPoseAuditMetric ToMetric()
                => new()
                {
                    Count = Count,
                    Average = Count > 0 ? Sum / Count : 0.0f,
                    Max = Max,
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

            var bodyPosition = new MetricAccumulator();
            var bodyRotation = new MetricAccumulator();
            var muscles = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var boneRotations = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var bonePositions = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);

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

                bodyPosition.Add(Vector3.Distance(referenceSample.BodyPosition.Value, actualSample.BodyPosition.Value));
                bodyRotation.Add(QuaternionAngleDegrees(referenceSample.BodyRotation.Value, actualSample.BodyRotation.Value));

                AccumulateNamedFloatErrors(referenceSample.Muscles, actualSample.Muscles, muscles);
                AccumulateBoneErrors(referenceSample.Bones, actualSample.Bones, boneRotations, bonePositions);
            }

            report.ComparedSamples = comparedSamples;

            report.BodyPositionError = bodyPosition.ToMetric();
            report.BodyRotationErrorDegrees = bodyRotation.ToMetric();
            report.MuscleAbsoluteError = ToMetricEntries(muscles);
            report.BoneLocalRotationErrorDegrees = ToMetricEntries(boneRotations);
            report.BoneRootSpacePositionError = ToMetricEntries(bonePositions);
            return report;
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
            IReadOnlyList<HumanoidPoseAuditNamedFloat> reference,
            IReadOnlyList<HumanoidPoseAuditNamedFloat> actual,
            Dictionary<string, MetricAccumulator> accumulators)
        {
            var actualByName = ToCanonicalNamedFloatDictionary(actual);
            foreach (var entry in reference)
            {
                string canonicalName = CanonicalizeMuscleName(entry.Name);
                if (!actualByName.TryGetValue(canonicalName, out float actualValue))
                    continue;

                GetOrAdd(accumulators, canonicalName).Add(Math.Abs(entry.Value - actualValue));
            }
        }

        private static void AccumulateBoneErrors(
            IReadOnlyList<HumanoidPoseAuditBoneSample> reference,
            IReadOnlyList<HumanoidPoseAuditBoneSample> actual,
            Dictionary<string, MetricAccumulator> rotationAccumulators,
            Dictionary<string, MetricAccumulator> positionAccumulators)
        {
            var actualByName = actual.ToDictionary(static x => x.Name, StringComparer.Ordinal);
            foreach (var entry in reference)
            {
                if (!actualByName.TryGetValue(entry.Name, out var actualBone))
                    continue;

                GetOrAdd(rotationAccumulators, entry.Name)
                    .Add(QuaternionAngleDegrees(entry.LocalRotation.Value, actualBone.LocalRotation.Value));
                GetOrAdd(positionAccumulators, entry.Name)
                    .Add(Vector3.Distance(entry.RootSpacePosition.Value, actualBone.RootSpacePosition.Value));
            }
        }

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
            if (!UnityHumanoidMuscleMap.TryGetValue(name, out var value))
                return name;

            return UnityHumanoidMuscleMap.TryGetHumanTraitName(value, out string humanTraitName)
                ? humanTraitName
                : value.ToString();
        }
    }
}
