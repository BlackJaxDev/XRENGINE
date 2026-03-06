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

            int comparedSamples = Math.Min(reference.Samples.Count, actual.Samples.Count);
            report.ComparedSamples = comparedSamples;

            var bodyPosition = new MetricAccumulator();
            var bodyRotation = new MetricAccumulator();
            var muscles = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var boneRotations = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
            var bonePositions = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);

            bool timeMismatchLogged = false;
            for (int i = 0; i < comparedSamples; i++)
            {
                HumanoidPoseAuditSample referenceSample = reference.Samples[i];
                HumanoidPoseAuditSample actualSample = actual.Samples[i];

                if (!timeMismatchLogged && Math.Abs(referenceSample.TimeSeconds - actualSample.TimeSeconds) > 0.0001f)
                {
                    timeMismatchLogged = true;
                    report.Warnings.Add($"Sample time mismatch at index {i}: reference={referenceSample.TimeSeconds:F6}, actual={actualSample.TimeSeconds:F6}.");
                }

                bodyPosition.Add(Vector3.Distance(referenceSample.BodyPosition.Value, actualSample.BodyPosition.Value));
                bodyRotation.Add(QuaternionAngleDegrees(referenceSample.BodyRotation.Value, actualSample.BodyRotation.Value));

                AccumulateNamedFloatErrors(referenceSample.Muscles, actualSample.Muscles, muscles);
                AccumulateBoneErrors(referenceSample.Bones, actualSample.Bones, boneRotations, bonePositions);
            }

            report.BodyPositionError = bodyPosition.ToMetric();
            report.BodyRotationErrorDegrees = bodyRotation.ToMetric();
            report.MuscleAbsoluteError = ToMetricEntries(muscles);
            report.BoneLocalRotationErrorDegrees = ToMetricEntries(boneRotations);
            report.BoneRootSpacePositionError = ToMetricEntries(bonePositions);
            return report;
        }

        private static void AccumulateNamedFloatErrors(
            IReadOnlyList<HumanoidPoseAuditNamedFloat> reference,
            IReadOnlyList<HumanoidPoseAuditNamedFloat> actual,
            Dictionary<string, MetricAccumulator> accumulators)
        {
            var actualByName = actual.ToDictionary(static x => x.Name, static x => x.Value, StringComparer.Ordinal);
            foreach (var entry in reference)
            {
                if (!actualByName.TryGetValue(entry.Name, out float actualValue))
                    continue;

                GetOrAdd(accumulators, entry.Name).Add(Math.Abs(entry.Value - actualValue));
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
    }
}
