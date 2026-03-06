using XREngine.Components;

namespace XREngine.Components.Animation
{
    public sealed class HumanoidPoseAuditComponent : XRComponent
    {
        private bool _completed;

        private AnimationClipComponent? _targetClipComponent;
        public AnimationClipComponent? TargetClipComponent
        {
            get => _targetClipComponent;
            set => SetField(ref _targetClipComponent, value);
        }

        private HumanoidComponent? _targetHumanoid;
        public HumanoidComponent? TargetHumanoid
        {
            get => _targetHumanoid;
            set => SetField(ref _targetHumanoid, value);
        }

        private bool _autoRunOnActivate = true;
        public bool AutoRunOnActivate
        {
            get => _autoRunOnActivate;
            set => SetField(ref _autoRunOnActivate, value);
        }

        private string _outputPath = string.Empty;
        public string OutputPath
        {
            get => _outputPath;
            set => SetField(ref _outputPath, value);
        }

        private string? _referencePath;
        public string? ReferencePath
        {
            get => _referencePath;
            set => SetField(ref _referencePath, value);
        }

        private string? _comparisonOutputPath;
        public string? ComparisonOutputPath
        {
            get => _comparisonOutputPath;
            set => SetField(ref _comparisonOutputPath, value);
        }

        private int _sampleRateOverride;
        public int SampleRateOverride
        {
            get => _sampleRateOverride;
            set => SetField(ref _sampleRateOverride, Math.Max(0, value));
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (AutoRunOnActivate && !_completed)
                RegisterTick(ETickGroup.Late, ETickOrder.Scene, TryRunAudit);
        }

        protected internal override void OnComponentDeactivated()
        {
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, TryRunAudit);
            base.OnComponentDeactivated();
        }

        public void RunAuditNow()
            => TryRunAudit();

        private void TryRunAudit()
        {
            if (_completed)
                return;

            var clipComponent = TargetClipComponent ?? GetSiblingComponent<AnimationClipComponent>();
            var humanoid = TargetHumanoid ?? GetSiblingComponent<HumanoidComponent>();
            if (clipComponent?.Animation is null || humanoid is null)
                return;

            if (humanoid.Hips.Node is null)
                humanoid.SetFromNode();
            if (humanoid.Hips.Node is null)
                return;

            HumanoidPoseAuditReport export = HumanoidPoseAuditSampler.Sample(clipComponent, humanoid, SampleRateOverride);

            if (!string.IsNullOrWhiteSpace(OutputPath))
                HumanoidPoseAuditIO.SaveReport(OutputPath, export);

            string? comparisonOutputPath = null;
            if (!string.IsNullOrWhiteSpace(ReferencePath))
            {
                if (File.Exists(ReferencePath))
                {
                    HumanoidPoseAuditReport reference = HumanoidPoseAuditIO.LoadReport(ReferencePath);
                    var comparison = HumanoidPoseAuditComparer.Compare(reference, export, ReferencePath, OutputPath);
                    comparisonOutputPath = ResolveComparisonOutputPath();
                    if (!string.IsNullOrWhiteSpace(comparisonOutputPath))
                        HumanoidPoseAuditIO.SaveComparison(comparisonOutputPath, comparison);

                    LogComparisonSummary(export, comparison, comparisonOutputPath);
                }
                else
                {
                    Debug.LogWarning($"[HumanoidPoseAudit] Reference report not found at '{ReferencePath}'. Export was still written.");
                }
            }
            else
            {
                string outputLabel = string.IsNullOrWhiteSpace(OutputPath) ? "<memory-only>" : Path.GetFullPath(OutputPath);
                Debug.Out($"[HumanoidPoseAudit] Exported {export.SampleCount} samples for '{export.ClipName}' to '{outputLabel}'.");
            }

            _completed = true;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, TryRunAudit);
        }

        private string? ResolveComparisonOutputPath()
        {
            if (!string.IsNullOrWhiteSpace(ComparisonOutputPath))
                return ComparisonOutputPath;

            if (string.IsNullOrWhiteSpace(OutputPath))
                return null;

            string fullPath = Path.GetFullPath(OutputPath);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(fullPath);
            return Path.Combine(directory, $"{fileName}.comparison.json");
        }

        private static void LogComparisonSummary(
            HumanoidPoseAuditReport export,
            HumanoidPoseAuditComparisonReport comparison,
            string? comparisonOutputPath)
        {
            var topBoneRotation = comparison.BoneLocalRotationErrorDegrees.FirstOrDefault();
            var topBonePosition = comparison.BoneRootSpacePositionError.FirstOrDefault();
            var topMuscle = comparison.MuscleAbsoluteError.FirstOrDefault();

            Debug.Out(
                $"[HumanoidPoseAudit] Compared {comparison.ComparedSamples} samples for '{export.ClipName}'. " +
                $"bodyPosMax={comparison.BodyPositionError.Max:F4}m, " +
                $"bodyRotMax={comparison.BodyRotationErrorDegrees.Max:F2}deg, " +
                $"worstBoneRot={(topBoneRotation?.Name ?? "<none>")}:{topBoneRotation?.Metric.Max ?? 0.0f:F2}deg, " +
                $"worstBonePos={(topBonePosition?.Name ?? "<none>")}:{topBonePosition?.Metric.Max ?? 0.0f:F4}m, " +
                $"worstMuscle={(topMuscle?.Name ?? "<none>")}:{topMuscle?.Metric.Max ?? 0.0f:F4}" +
                $"{(string.IsNullOrWhiteSpace(comparisonOutputPath) ? string.Empty : $" -> '{Path.GetFullPath(comparisonOutputPath)}'")}");
        }
    }
}
