using Newtonsoft.Json;

namespace XREngine.Components.Animation
{
    public sealed class HumanoidPoseAuditReport
    {
        /// <summary>
        /// Schema 7 makes the conformance values explicit.  Values whose names end in
        /// <c>ModelRootMeters</c> use the same right-handed, Y-up, model-root coordinate
        /// system in Unity and XRENGINE; raw importer channels remain diagnostic only.
        /// </summary>
        public const int CurrentSchemaVersion = 7;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Source { get; set; } = string.Empty;
        public string ClipName { get; set; } = string.Empty;
        public string AvatarName { get; set; } = string.Empty;
        public float DurationSeconds { get; set; }
        public int SampleRate { get; set; }
        public int SampleCount { get; set; }
        public float AvatarHumanScale { get; set; }
        /// <summary>
        /// XRENGINE world units represented by one Unity meter for the calibrated avatar.
        /// Unity reference reports leave this at zero; cross-runtime comparison reads it
        /// from the XRENGINE report.
        /// </summary>
        public float EngineUnitsPerSourceMeter { get; set; }
        public string BodyChannelSpace { get; set; } = "Diagnostic importer-mapped normalized humanoid body space; not a conformance metric";
        public string CommonPoseSpace { get; set; } = "Right-handed model-root space in meters (+X right, +Y up, +Z forward); rotations are right-handed relative to that root.";
        public string BoneRootSpace { get; set; } = "Humanoid component scene-node local space";
        public string BoneWorldSpace { get; set; } = "XRENGINE world space";
        /// <summary>Roles which each sample must contain exactly once for schema-7 comparison.</summary>
        public List<string> RequiredBoneRoles { get; set; } = [];
        /// <summary>Humanoid channels which each sample must contain exactly once for schema-7 comparison.</summary>
        public List<string> RequiredMuscleChannels { get; set; } = [];
        public List<HumanoidPoseAuditNamedFloatRange> MuscleDefaultRanges { get; set; } = [];
        public List<HumanoidPoseAuditMuscleProbe> MuscleProbes { get; set; } = [];
        public HumanoidPoseAuditSample? DefaultMusclePose { get; set; }
        public List<HumanoidPoseAuditSample> Samples { get; set; } = [];
    }

    public sealed class HumanoidPoseAuditSample
    {
        public int Index { get; set; }
        public float TimeSeconds { get; set; }
        public HumanoidPoseAuditVector3 BodyPosition { get; set; } = new();
        public HumanoidPoseAuditQuaternion BodyRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        /// <summary>Comparable solved Body pose in <see cref="HumanoidPoseAuditReport.CommonPoseSpace"/>.</summary>
        public bool HasSolvedBodyModelRootPose { get; set; }
        public HumanoidPoseAuditVector3 SolvedBodyModelRootPositionMeters { get; set; } = new();
        public HumanoidPoseAuditQuaternion SolvedBodyModelRootRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        /// <summary>
        /// Imported RootT body-center sample after Unity-to-XRENGINE axis conversion but
        /// before avatar-scale or Hips composition. <see cref="BodyPosition"/> is retained
        /// as a compatibility alias for existing comparison tooling.
        /// </summary>
        public HumanoidPoseAuditVector3 ImportedMappedBodyPosition { get; set; } = new();
        /// <summary>
        /// Imported RootQ body-orientation sample after Unity-to-XRENGINE axis conversion but
        /// before bind-relative Hips composition. <see cref="BodyRotation"/> is retained as a
        /// compatibility alias for existing comparison tooling.
        /// </summary>
        public HumanoidPoseAuditQuaternion ImportedMappedBodyRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public int ImportedMappedBodyChannels { get; set; }
        public HumanoidPoseAuditVector3 CanonicalImportedMappedBodyPosition { get; set; } = new();
        public HumanoidPoseAuditQuaternion CanonicalImportedMappedBodyRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public int CanonicalImportedMappedBodyChannels { get; set; }
        public HumanoidPoseAuditVector3 ConvertedBodyTranslationDelta { get; set; } = new();
        public HumanoidPoseAuditQuaternion ConvertedBodyRotationDelta { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public HumanoidPoseAuditVector3 ProjectedRootPosition { get; set; } = new();
        public HumanoidPoseAuditQuaternion ProjectedRootRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public int ProjectedRootChannels { get; set; }
        public HumanoidPoseAuditVector3 TemporalRootMotionTranslation { get; set; } = new();
        public HumanoidPoseAuditQuaternion TemporalRootMotionRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public int TemporalRootMotionChannels { get; set; }
        /// <summary>Unity exporter compatibility alias for temporal root translation.</summary>
        public HumanoidPoseAuditVector3? RootMotionDeltaPosition { get; set; }
        /// <summary>Unity exporter compatibility alias for temporal root rotation.</summary>
        public HumanoidPoseAuditQuaternion? RootMotionDeltaRotation { get; set; }
        public HumanoidPoseAuditVector3? ComposedHipsLocalPosition { get; set; }
        public HumanoidPoseAuditQuaternion? ComposedHipsLocalRotation { get; set; }
        /// <summary>Unity exporter compatibility alias for the composed Hips local position.</summary>
        public HumanoidPoseAuditVector3? HipsLocalPosition { get; set; }
        /// <summary>Unity exporter compatibility alias for the composed Hips local rotation.</summary>
        public HumanoidPoseAuditQuaternion? HipsLocalRotation { get; set; }
        /// <summary>Comparable final Hips pose relative to the model root, in meters.</summary>
        public HumanoidPoseAuditVector3? HipsModelRootPositionMeters { get; set; }
        public HumanoidPoseAuditQuaternion? HipsModelRootRotation { get; set; }
        /// <summary>Captured Hips world pose in the canonical coordinate convention, in meters.</summary>
        public HumanoidPoseAuditVector3? HipsWorldPositionMeters { get; set; }
        public HumanoidPoseAuditQuaternion? HipsWorldRotation { get; set; }
        public HumanoidPoseAuditVector3 CharacterRootLocalPosition { get; set; } = new();
        public HumanoidPoseAuditQuaternion CharacterRootLocalRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public HumanoidPoseAuditVector3 CharacterRootWorldPosition { get; set; } = new();
        public HumanoidPoseAuditQuaternion CharacterRootWorldRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        /// <summary>Optional native body-frame derivation trace for this sample.</summary>
        public HumanoidPoseAuditBodyFrame? NativeBodyFrame { get; set; }
        public List<HumanoidPoseAuditNamedFloat> Muscles { get; set; } = [];
        public List<HumanoidPoseAuditRawCurveSample> RawCurves { get; set; } = [];
        public List<HumanoidPoseAuditBoneSample> Bones { get; set; } = [];
    }

    public sealed class HumanoidPoseAuditNamedFloat
    {
        public string Name { get; set; } = string.Empty;
        public float Value { get; set; }
    }

    public sealed class HumanoidPoseAuditNamedFloatRange
    {
        public string Name { get; set; } = string.Empty;
        public float Min { get; set; }
        public float Max { get; set; }
    }

    public sealed class HumanoidPoseAuditRawCurveSample
    {
        public string Path { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public float Value { get; set; }
    }

    public sealed class HumanoidPoseAuditBoneSample
    {
        public string Name { get; set; } = string.Empty;
        public HumanoidPoseAuditVector3 LocalPosition { get; set; } = new();
        public HumanoidPoseAuditQuaternion LocalRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public HumanoidPoseAuditQuaternion BindRelativeRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public HumanoidPoseAuditQuaternion NeutralBindRelativeRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public HumanoidPoseAuditQuaternion PoseDeltaFromNeutralRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public HumanoidPoseAuditVector3 RootSpacePosition { get; set; } = new();
        public HumanoidPoseAuditVector3 WorldPosition { get; set; } = new();
        public HumanoidPoseAuditQuaternion WorldRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        /// <summary>Comparable model-root position in meters.</summary>
        public HumanoidPoseAuditVector3 ModelRootPositionMeters { get; set; } = new();
        public HumanoidPoseAuditQuaternion ModelRootRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    }

    public sealed class HumanoidPoseAuditVector3
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        [JsonIgnore]
        public System.Numerics.Vector3 Value
        {
            get => new(X, Y, Z);
            set
            {
                X = value.X;
                Y = value.Y;
                Z = value.Z;
            }
        }

        public static HumanoidPoseAuditVector3 From(System.Numerics.Vector3 value)
            => new() { Value = value };
    }

    public sealed class HumanoidPoseAuditQuaternion
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; } = 1.0f;

        [JsonIgnore]
        public System.Numerics.Quaternion Value
        {
            get => new(X, Y, Z, W);
            set
            {
                X = value.X;
                Y = value.Y;
                Z = value.Z;
                W = value.W;
            }
        }

        public static HumanoidPoseAuditQuaternion Identity => new();

        public static HumanoidPoseAuditQuaternion From(System.Numerics.Quaternion value)
            => new() { Value = System.Numerics.Quaternion.Normalize(value) };

        /// <summary>Preserves an imported quaternion's scalar components without normalization.</summary>
        public static HumanoidPoseAuditQuaternion FromRaw(System.Numerics.Quaternion value)
            => new() { Value = value };
    }

    public sealed class HumanoidPoseAuditComparisonReport
    {
        public int SchemaVersion { get; set; } = HumanoidPoseAuditReport.CurrentSchemaVersion;
        public string? ReferencePath { get; set; }
        public string? ActualPath { get; set; }
        public int ComparedSamples { get; set; }
        public List<string> Warnings { get; set; } = [];
        /// <summary>Structural schema failures. These are deliberately distinct from informational warnings.</summary>
        public List<HumanoidPoseAuditComparisonFailure> Failures { get; set; } = [];
        public HumanoidPoseAuditMetric BodyPositionError { get; set; } = new();
        public HumanoidPoseAuditMetric BodyRotationErrorDegrees { get; set; } = new();
        public HumanoidPoseAuditMetric ProjectedRootPositionError { get; set; } = new();
        public HumanoidPoseAuditMetric ProjectedRootRotationErrorDegrees { get; set; } = new();
        public HumanoidPoseAuditMetric TemporalRootMotionTranslationError { get; set; } = new();
        public HumanoidPoseAuditMetric TemporalRootMotionRotationErrorDegrees { get; set; } = new();
        public HumanoidPoseAuditMetric ComposedHipsLocalPositionError { get; set; } = new();
        public HumanoidPoseAuditMetric ComposedHipsLocalRotationErrorDegrees { get; set; } = new();
        public HumanoidPoseAuditMetric SolvedBodyModelRootPositionErrorMeters { get; set; } = new();
        public HumanoidPoseAuditMetric SolvedBodyModelRootRotationErrorDegrees { get; set; } = new();
        public HumanoidPoseAuditMetric HipsModelRootPositionErrorMeters { get; set; } = new();
        public HumanoidPoseAuditMetric HipsModelRootRotationErrorDegrees { get; set; } = new();
        public HumanoidPoseAuditMetric HipsWorldPositionErrorMeters { get; set; } = new();
        public HumanoidPoseAuditMetric HipsWorldRotationErrorDegrees { get; set; } = new();
        public List<HumanoidPoseAuditMetricEntry> MuscleAbsoluteError { get; set; } = [];
        public List<HumanoidPoseAuditMetricEntry> BoneLocalPositionError { get; set; } = [];
        public List<HumanoidPoseAuditMetricEntry> BoneLocalRotationErrorDegrees { get; set; } = [];
        public List<HumanoidPoseAuditMetricEntry> BoneRootSpacePositionError { get; set; } = [];
        public List<HumanoidPoseAuditMetricEntry> BoneModelRootPositionErrorMeters { get; set; } = [];
        public List<HumanoidPoseAuditMetricEntry> BoneWorldRotationErrorDegrees { get; set; } = [];
    }

    public sealed class HumanoidPoseAuditComparisonFailure
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int? SampleIndex { get; set; }
    }

    public sealed class HumanoidPoseAuditMetricEntry
    {
        public string Name { get; set; } = string.Empty;
        public HumanoidPoseAuditMetric Metric { get; set; } = new();
    }

    public sealed class HumanoidPoseAuditMetric
    {
        public int Count { get; set; }
        public float Average { get; set; }
        public float Max { get; set; }
        public HumanoidPoseAuditWorstSample? WorstSample { get; set; }
    }

    /// <summary>Identifies the aligned sample pair that produced a metric's maximum error.</summary>
    public sealed class HumanoidPoseAuditWorstSample
    {
        public int ReferenceIndex { get; set; }
        public float ReferenceTimeSeconds { get; set; }
        public int ActualIndex { get; set; }
        public float ActualTimeSeconds { get; set; }
    }
}
