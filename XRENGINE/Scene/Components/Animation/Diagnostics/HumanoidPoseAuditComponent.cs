using XREngine.Components;
using XREngine.Core.Reflection.Attributes;

namespace XREngine.Components.Animation
{
    public sealed class HumanoidPoseAuditComponent : XRComponent
    {
        private bool _completed;

        private AnimationClipComponent? _targetClipComponent;
        /// <summary>
        /// The AnimationClipComponent to audit. If not set, the audit will attempt to find a sibling AnimationClipComponent.
        /// </summary>
        public AnimationClipComponent? TargetClipComponent
        {
            get => _targetClipComponent;
            set => SetField(ref _targetClipComponent, value);
        }

        private HumanoidComponent? _targetHumanoid;
        /// <summary>
        /// The humanoid component to audit. If not set, the audit will attempt to find a sibling HumanoidComponent.
        /// </summary>
        public HumanoidComponent? TargetHumanoid
        {
            get => _targetHumanoid;
            set => SetField(ref _targetHumanoid, value);
        }

        private bool _autoRunOnActivate = true;
        /// <summary>
        /// If true, the audit will automatically run when the component is activated and the required references are available.
        /// </summary>
        public bool AutoRunOnActivate
        {
            get => _autoRunOnActivate;
            set => SetField(ref _autoRunOnActivate, value);
        }

        private string _outputPath = string.Empty;
        /// <summary>
        /// If set, the audit report will be saved to this path. 
        /// Can be relative to the current working directory or absolute. 
        /// If not set, the report will only exist in memory and be logged to the console.
        /// </summary>
        [InspectorPath(InspectorPathKind.File, InspectorPathFormat.Both, DialogMode = InspectorPathDialogMode.Save, Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Title = "Choose Audit Output Path")]
        public string OutputPath
        {
            get => _outputPath;
            set => SetField(ref _outputPath, value);
        }

        private string? _referencePath = "K:\\Unity\\Jax Main Avatars\\PoseAudit\\UnityHumanoidPose.json";
        /// <summary>
        /// If set, the audit report at this path will be loaded and compared against the generated report, with a summary of the comparison logged to the console.
        /// Can be relative to the current working directory or absolute.
        /// </summary>
        [InspectorPath(InspectorPathKind.File, InspectorPathFormat.Both, DialogMode = InspectorPathDialogMode.Open, Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Title = "Choose Reference Audit Path")]
        public string? ReferencePath
        {
            get => _referencePath;
            set => SetField(ref _referencePath, value);
        }

        private string? _comparisonOutputPath;
        /// <summary>
        /// If set, the comparison report will be saved to this path.
        /// Can be relative to the current working directory or absolute.
        /// </summary>
        [InspectorPath(InspectorPathKind.File, InspectorPathFormat.Both, DialogMode = InspectorPathDialogMode.Save, Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Title = "Choose Comparison Output Path")]
        public string? ComparisonOutputPath
        {
            get => _comparisonOutputPath;
            set => SetField(ref _comparisonOutputPath, value);
        }

        private int _sampleRateOverride;
        /// <summary>
        /// If set, this value will override the default sample rate for the audit.
        /// Must be a non-negative integer.
        /// </summary>
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

            HumanoidPoseAuditReport? reference = LoadReferenceReportIfAvailable();
            int sampleRate = ResolveSampleRate(reference);
            HumanoidPoseAuditReport export = HumanoidPoseAuditSampler.Sample(clipComponent, humanoid, sampleRate);
            string outputPath = ResolveOutputPath(export);

            HumanoidPoseAuditIO.SaveReport(outputPath, export);

            string? comparisonOutputPath = null;
            if (!string.IsNullOrWhiteSpace(ReferencePath))
            {
                if (reference is not null)
                {
                    var comparison = HumanoidPoseAuditComparer.Compare(reference, export, ReferencePath, outputPath);
                    comparisonOutputPath = ResolveComparisonOutputPath(outputPath);
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
                Debug.Out($"[HumanoidPoseAudit] Exported {export.SampleCount} samples for '{export.ClipName}' to '{Path.GetFullPath(outputPath)}'.");
            }

            _completed = true;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, TryRunAudit);
        }

        private HumanoidPoseAuditReport? LoadReferenceReportIfAvailable()
            => string.IsNullOrWhiteSpace(ReferencePath) || !File.Exists(ReferencePath) ? (HumanoidPoseAuditReport?)null : HumanoidPoseAuditIO.LoadReport(ReferencePath);

        private int ResolveSampleRate(HumanoidPoseAuditReport? reference)
        {
            if (SampleRateOverride > 0)
                return SampleRateOverride;

            if (reference?.SampleRate > 0)
                return reference.SampleRate;

            return 0;
        }

        private string ResolveOutputPath(HumanoidPoseAuditReport export)
        {
            if (!string.IsNullOrWhiteSpace(OutputPath))
                return OutputPath;

            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string fileName = BuildDefaultAuditFileName(export.ClipName, ".json");
            return Path.Combine(desktopPath, fileName);
        }

        private string ResolveComparisonOutputPath(string outputPath)
        {
            if (!string.IsNullOrWhiteSpace(ComparisonOutputPath))
                return ComparisonOutputPath;

            string fullPath = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(fullPath);
            return Path.Combine(directory, $"{fileName}.comparison.json");
        }

        private static string BuildDefaultAuditFileName(string? clipName, string extension)
        {
            string baseName = string.IsNullOrWhiteSpace(clipName)
                ? "humanoid_pose_audit"
                : $"{clipName}_humanoid_pose_audit";

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(invalidChar, '_');

            return $"{baseName}{extension}";
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
