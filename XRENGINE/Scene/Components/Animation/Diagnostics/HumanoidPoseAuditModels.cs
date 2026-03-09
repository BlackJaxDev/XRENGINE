using Newtonsoft.Json;

namespace XREngine.Components.Animation
{
    public sealed class HumanoidPoseAuditReport
    {
        public const int CurrentSchemaVersion = 3;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Source { get; set; } = string.Empty;
        public string ClipName { get; set; } = string.Empty;
        public string AvatarName { get; set; } = string.Empty;
        public float DurationSeconds { get; set; }
        public int SampleRate { get; set; }
        public int SampleCount { get; set; }
        public List<HumanoidPoseAuditNamedFloatRange> MuscleDefaultRanges { get; set; } = [];
        public HumanoidPoseAuditSample? DefaultMusclePose { get; set; }
        public List<HumanoidPoseAuditSample> Samples { get; set; } = [];
    }

    public sealed class HumanoidPoseAuditSample
    {
        public int Index { get; set; }
        public float TimeSeconds { get; set; }
        public HumanoidPoseAuditVector3 BodyPosition { get; set; } = new();
        public HumanoidPoseAuditQuaternion BodyRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
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
        public HumanoidPoseAuditQuaternion LocalRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public HumanoidPoseAuditQuaternion BindRelativeRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
        public HumanoidPoseAuditVector3 RootSpacePosition { get; set; } = new();
        public HumanoidPoseAuditVector3 WorldPosition { get; set; } = new();
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
    }

    public sealed class HumanoidPoseAuditComparisonReport
    {
        public int SchemaVersion { get; set; } = HumanoidPoseAuditReport.CurrentSchemaVersion;
        public string? ReferencePath { get; set; }
        public string? ActualPath { get; set; }
        public int ComparedSamples { get; set; }
        public List<string> Warnings { get; set; } = [];
        public HumanoidPoseAuditMetric BodyPositionError { get; set; } = new();
        public HumanoidPoseAuditMetric BodyRotationErrorDegrees { get; set; } = new();
        public List<HumanoidPoseAuditMetricEntry> MuscleAbsoluteError { get; set; } = [];
        public List<HumanoidPoseAuditMetricEntry> BoneLocalRotationErrorDegrees { get; set; } = [];
        public List<HumanoidPoseAuditMetricEntry> BoneRootSpacePositionError { get; set; } = [];
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
    }
}
