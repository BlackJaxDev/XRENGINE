using System;
using System.Collections.Generic;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

[ExecuteAlways]
public sealed class HumanoidPoseAuditExporter : MonoBehaviour
{
    [Serializable]
    private sealed class PoseAuditReport
    {
        public int SchemaVersion = 6;
        public string Source = "UnityMecanim";
        public string ClipName = string.Empty;
        public string AvatarName = string.Empty;
        public string BodyPositionSpace = "HumanPose bodyPosition: world-space center of mass normalized by Animator.humanScale";
        public string BoneRootSpace = "Animator GameObject local space";
        public string BoneWorldSpace = "Unity world space";
        public float DurationSeconds;
        public int SampleRate;
        public int SampleCount;
        public float AvatarHumanScale;
        public bool AnimatorApplyRootMotion;
        public AvatarHumanDescriptionSettings AvatarSettings = new();
        public ClipRootMotionSettings RootMotionSettings = new();
        public List<NamedFloatRange> MuscleDefaultRanges = new();
        public List<MuscleProbe> MuscleProbes = new();
        public PoseAuditSample DefaultMusclePose;
        public List<PoseAuditSample> Samples = new();
    }

    [Serializable]
    private sealed class PoseAuditSample
    {
        public int Index;
        public float TimeSeconds;
        public PoseVector3 BodyPosition = new();
        public PoseQuaternion BodyRotation = new();
        public PoseVector3 CharacterRootLocalPosition = new();
        public PoseQuaternion CharacterRootLocalRotation = new();
        public PoseVector3 CharacterRootWorldPosition = new();
        public PoseQuaternion CharacterRootWorldRotation = new();
        public PoseVector3 ProjectedRootPosition = new();
        public PoseQuaternion ProjectedRootRotation = new();
        public PoseVector3 RootMotionDeltaPosition = new();
        public PoseQuaternion RootMotionDeltaRotation = new();
        public PoseVector3 HipsLocalPosition = new();
        public PoseQuaternion HipsLocalRotation = new();
        public float LeftFeetBottomHeight;
        public float RightFeetBottomHeight;
        public List<NamedFloat> Muscles = new();
        public List<RawCurveSample> RawCurves = new();
        public List<BoneSample> Bones = new();
    }

    [Serializable]
    private sealed class ClipRootMotionSettings
    {
        public float StartTime;
        public float StopTime;
        public float OrientationOffsetY;
        public float Level;
        public float CycleOffset;
        public bool LoopTime;
        public bool LoopBlend;
        public bool BakeOrientationIntoPose;
        public bool BakePositionYIntoPose;
        public bool BakePositionXZIntoPose;
        public bool KeepOriginalOrientation;
        public bool KeepOriginalPositionY;
        public bool KeepOriginalPositionXZ;
        public bool HeightFromFeet;
        public bool Mirror;
    }

    [Serializable]
    private sealed class NamedFloat
    {
        public string Name = string.Empty;
        public float Value;
    }

    [Serializable]
    private sealed class NamedFloatRange
    {
        public string Name = string.Empty;
        public float Min;
        public float Max;
    }

    [Serializable]
    private sealed class AvatarHumanDescriptionSettings
    {
        public float UpperArmTwist;
        public float LowerArmTwist;
        public float UpperLegTwist;
        public float LowerLegTwist;
        public float ArmStretch;
        public float LegStretch;
        public float FeetSpacing;
        public bool HasTranslationDoF;
    }

    [Serializable]
    private sealed class MuscleProbe
    {
        public int Index;
        public string Name = string.Empty;
        public List<MuscleProbeBone> Bones = new();
    }

    [Serializable]
    private sealed class MuscleProbeBone
    {
        public string Name = string.Empty;
        public PoseQuaternion NegativePoseDeltaFromNeutralRotation = new();
        public PoseQuaternion PositivePoseDeltaFromNeutralRotation = new();
    }

    [Serializable]
    private sealed class AvatarProfileReport
    {
        /// <summary>
        /// Version 5 adds Unity's Hips-parent allocation frame so runtime policy
        /// composition occurs in the same coordinate system as Mecanim.
        /// The pose-audit report intentionally remains schema 6.
        /// </summary>
        public int SchemaVersion = 5;
        public string Source = "UnityMecanim";
        public string AvatarName = string.Empty;
        public float HumanScale;
        public string CalibrationClipName = string.Empty;
        public ClipRootMotionSettings CalibrationRootMotionSettings = new();
        public AvatarProfileRootAllocationFrame RootAllocationFrame = new();
        public int CalibrationClipTrainingStride = 2;
        public AvatarHumanDescriptionSettings AvatarSettings = new();
        public AvatarProfileBodyAxes BodyAxes = new();
        public List<AvatarProfileRole> AvatarRoles = new();
        public List<AvatarProfileTwistChain> TwistChains = new();
        public List<AvatarProfileNeutralBone> NeutralPoseBoneRotations = new();
        public List<AvatarProfileMuscleResponse> MuscleResponses = new();
        public List<AvatarProfileCoupledMuscleCalibration> CoupledMuscleCalibrations = new();
    }

    [Serializable]
    private sealed class AvatarProfileNeutralBone
    {
        public string Name = string.Empty;
        public PoseVector3 LocalPosition = new();
        public PoseQuaternion BindRelativeRotation = new();
    }

    [Serializable]
    private sealed class AvatarProfileMuscleResponse
    {
        public int MuscleIndex;
        public string MuscleName = string.Empty;
        public string BoneName = string.Empty;
        public PoseQuaternion NegativePoseDeltaFromNeutralRotation = new();
        public PoseQuaternion PositivePoseDeltaFromNeutralRotation = new();
    }

    [Serializable]
    private sealed class AvatarProfileBodyAxes
    {
        public PoseVector3 Right = PoseVector3.From(Vector3.right);
        public PoseVector3 Up = PoseVector3.From(Vector3.up);
        public PoseVector3 Forward = PoseVector3.From(Vector3.forward);
        public string CoordinateSpace = "Unity Animator root local space";
    }

    [Serializable]
    private sealed class AvatarProfileRootAllocationFrame
    {
        public PoseVector3 HipsParentPositionInAnimatorRoot = new();
        public PoseQuaternion HipsParentRotationInAnimatorRoot = new();
        public PoseVector3 HipsParentScaleInAnimatorRoot = PoseVector3.From(Vector3.one);
    }

    [Serializable]
    private sealed class AvatarProfileRole
    {
        public string HumanName = string.Empty;
        public string TransformName = string.Empty;
        public bool Required;
    }

    [Serializable]
    private sealed class AvatarProfileTwistChain
    {
        public string Name = string.Empty;
        public string ProximalRole = string.Empty;
        public string DistalRole = string.Empty;
        public string EndRole = string.Empty;
        public float ProximalDistribution;
        public float DistalDistribution;
    }

    /// <summary>
    /// Allocation-at-tool-time approximation of Unity's coupled humanoid-muscle
    /// response for one bone. Runtime evaluation uses the three ordered feature
    /// blocks x_i, x_i^2, x_i*abs(x_i), followed by pairs x_i*x_j for i &lt; j.
    /// There is deliberately no intercept: an all-zero muscle vector evaluates
    /// to the identity rotation vector and a zero local-position delta.
    /// </summary>
    [Serializable]
    private sealed class AvatarProfileCoupledMuscleCalibration
    {
        public string BoneName = string.Empty;
        public string FeatureContract = "linear x_i; square x_i^2; signed-square x_i*abs(x_i); pairwise x_i*x_j for i<j; then all monomials with replacement for degrees 3..MaximumPolynomialDegree; ordered by MuscleIndices; no intercept";
        public List<int> MuscleIndices = new();
        public List<string> MuscleNames = new();
        public int MaximumPolynomialDegree;
        public int FeatureCount;
        public int SampleCount;
        public int CombinationSampleCount;
        public int EndpointSampleCount;
        public int ReferenceMotionSampleCount;
        public float ReferenceMotionSampleWeight = 1.0f;
        public float RidgeLambda;
        public string RotationBaselineContract = "ordered shortest-arc single-muscle endpoint product";
        public string PositionBaselineContract = "ordered signed linear single-muscle endpoint sum";
        public List<PoseQuaternion> NegativeEndpointRotations = new();
        public List<PoseQuaternion> PositiveEndpointRotations = new();
        public List<PoseVector3> NegativeEndpointPositionDeltas = new();
        public List<PoseVector3> PositiveEndpointPositionDeltas = new();
        public List<float> XCoefficients = new();
        public List<float> YCoefficients = new();
        public List<float> ZCoefficients = new();
        public float MeanRotationVectorErrorRadians;
        public float MaxRotationVectorErrorRadians;
        public float MeanAngularErrorDegrees;
        public float MaxAngularErrorDegrees;
        public List<float> PositionXCoefficients = new();
        public List<float> PositionYCoefficients = new();
        public List<float> PositionZCoefficients = new();
        public string PositionErrorUnits = "Animator bone local units";
        public float MeanPositionError;
        public float MaxPositionError;
        public List<float> ProjectedRootYCoefficients = new();
        public int ProjectedRootYTrainingSampleCount;
        public float MeanProjectedRootYError;
        public float MaxProjectedRootYError;
        public float ProjectedRootYZeroOffset;
    }

    private sealed class CoupledCalibrationSample
    {
        public float[] Muscles;
        public float Weight = 1.0f;
        public bool HasProjectedRootY;
        public float ProjectedRootY;
        public Dictionary<string, Quaternion> BonePoseDeltas = new(StringComparer.Ordinal);
        public Dictionary<string, Vector3> BonePositionDeltas = new(StringComparer.Ordinal);
    }

    [Serializable]
    private sealed class RawCurveSample
    {
        public string Path = string.Empty;
        public string TypeName = string.Empty;
        public string PropertyName = string.Empty;
        public float Value;
    }

    [Serializable]
    private sealed class BoneSample
    {
        public string Name = string.Empty;
        public PoseVector3 LocalPosition = new();
        public PoseQuaternion LocalRotation = new();
        public PoseQuaternion BindRelativeRotation = new();
        public PoseQuaternion NeutralBindRelativeRotation = new();
        public PoseQuaternion PoseDeltaFromNeutralRotation = new();
        public PoseVector3 RootSpacePosition = new();
        public PoseVector3 WorldPosition = new();
    }

    [Serializable]
    private sealed class PoseVector3
    {
        public float X;
        public float Y;
        public float Z;

        public static PoseVector3 From(Vector3 value)
        {
            return new PoseVector3
            {
                X = value.x,
                Y = value.y,
                Z = value.z,
            };
        }
    }

    [Serializable]
    private sealed class PoseQuaternion
    {
        public float X;
        public float Y;
        public float Z;
        public float W = 1.0f;

        public static PoseQuaternion From(Quaternion value)
        {
            value = Quaternion.Normalize(value);
            return new PoseQuaternion
            {
                X = value.x,
                Y = value.y,
                Z = value.z,
                W = value.w,
            };
        }

        public Quaternion ToQuaternion()
        {
            return Quaternion.Normalize(new Quaternion(X, Y, Z, W));
        }
    }

    private readonly struct BoneDefinition
    {
        public readonly string Name;
        public readonly HumanBodyBones Bone;

        public BoneDefinition(string name, HumanBodyBones bone)
        {
            Name = name;
            Bone = bone;
        }
    }

    private sealed class RawCurveBinding
    {
        public string Path = string.Empty;
        public string TypeName = string.Empty;
        public string PropertyName = string.Empty;
        public AnimationCurve Curve = new AnimationCurve();
    }

    private static readonly BoneDefinition[] BonesToSample =
    {
        new("Hips", HumanBodyBones.Hips),
        new("Spine", HumanBodyBones.Spine),
        new("Chest", HumanBodyBones.Chest),
        new("UpperChest", HumanBodyBones.UpperChest),
        new("Neck", HumanBodyBones.Neck),
        new("Head", HumanBodyBones.Head),
        new("Jaw", HumanBodyBones.Jaw),
        new("LeftEye", HumanBodyBones.LeftEye),
        new("RightEye", HumanBodyBones.RightEye),
        new("LeftShoulder", HumanBodyBones.LeftShoulder),
        new("LeftUpperArm", HumanBodyBones.LeftUpperArm),
        new("LeftLowerArm", HumanBodyBones.LeftLowerArm),
        new("LeftHand", HumanBodyBones.LeftHand),
        new("RightShoulder", HumanBodyBones.RightShoulder),
        new("RightUpperArm", HumanBodyBones.RightUpperArm),
        new("RightLowerArm", HumanBodyBones.RightLowerArm),
        new("RightHand", HumanBodyBones.RightHand),
        new("LeftUpperLeg", HumanBodyBones.LeftUpperLeg),
        new("LeftLowerLeg", HumanBodyBones.LeftLowerLeg),
        new("LeftFoot", HumanBodyBones.LeftFoot),
        new("LeftToes", HumanBodyBones.LeftToes),
        new("RightUpperLeg", HumanBodyBones.RightUpperLeg),
        new("RightLowerLeg", HumanBodyBones.RightLowerLeg),
        new("RightFoot", HumanBodyBones.RightFoot),
        new("RightToes", HumanBodyBones.RightToes),
    };

    private static readonly int[] HaltonPrimes =
    {
        2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53,
        59, 61, 67, 71, 73, 79, 83, 89, 97, 101, 103, 107, 109, 113,
        127, 131, 137, 139, 149, 151, 157, 163, 167, 173, 179, 181,
        191, 193, 197, 199, 211, 223, 227, 229, 233, 239, 241, 251,
        257, 263, 269, 271, 277, 281, 283, 293, 307, 311, 313, 317,
        331, 337, 347, 349, 353, 359, 367, 373, 379, 383, 389, 397,
        401, 409, 419, 421, 431, 433, 439, 443, 449, 457, 461, 463,
    };

    public Animator Animator;
    public AnimationClip Clip;
    public string OutputPath = "PoseAudit/UnityHumanoidPose.json";
    public string AvatarProfileOutputPath = string.Empty;
    public int SampleRateOverride;

    [ContextMenu("Export Humanoid Pose Audit")]
    public void Export()
    {
        if (Animator == null)
            throw new InvalidOperationException("Assign an Animator.");
        if (Clip == null)
            throw new InvalidOperationException("Assign an AnimationClip.");
        if (!Animator.isHuman)
            throw new InvalidOperationException("Animator avatar must be humanoid.");

        ExportAnimator(Animator, Clip, OutputPath, AvatarProfileOutputPath, ResolveSampleRate(Clip));
    }

#if UNITY_EDITOR
    /// <summary>
    /// Batch entry point for reproducible reference capture. Expected command-line
    /// arguments are -poseAuditModel, -poseAuditClip, and -poseAuditOutput.
    /// The model must already import as a valid Unity Humanoid Avatar.
    /// </summary>
    public static void ExportBatch()
    {
        string modelPath = GetRequiredCommandLineArgument("-poseAuditModel");
        string clipPath = GetRequiredCommandLineArgument("-poseAuditClip");
        string outputPath = GetRequiredCommandLineArgument("-poseAuditOutput");
        string avatarProfileOutputPath = GetCommandLineArgument("-poseAuditAvatarProfileOutput");
        int sampleRate = GetOptionalIntCommandLineArgument("-poseAuditSampleRate");

        GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (modelAsset == null)
            throw new InvalidOperationException("Could not load humanoid model asset at '" + modelPath + "'.");

        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
        if (clip == null)
            throw new InvalidOperationException("Could not load animation clip asset at '" + clipPath + "'.");

        GameObject instance = UnityEngine.Object.Instantiate(modelAsset);
        instance.hideFlags = HideFlags.HideAndDontSave;
        instance.name = modelAsset.name + "_PoseAuditBatchSource";
        try
        {
            Animator animator = instance.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman)
                throw new InvalidOperationException("The batch model is missing a valid humanoid Animator/Avatar.");

            int resolvedSampleRate = sampleRate > 0
                ? sampleRate
                : Mathf.Max(1, Mathf.RoundToInt(clip.frameRate > 0.0f ? clip.frameRate : 30.0f));
            ExportAnimator(animator, clip, outputPath, avatarProfileOutputPath, resolvedSampleRate);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }
#endif

    private static void ExportAnimator(
        Animator sourceAnimator,
        AnimationClip clip,
        string outputPath,
        string avatarProfileOutputPath,
        int sampleRate)
    {
        GameObject clone = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
        clone.hideFlags = HideFlags.HideAndDontSave;
        clone.name = sourceAnimator.gameObject.name + "_PoseAuditClone";

        try
        {
            DisableBehaviours(clone);

            Animator cloneAnimator = clone.GetComponent<Animator>();
            if (cloneAnimator == null || !cloneAnimator.isHuman)
                throw new InvalidOperationException("Cloned Animator is missing or not humanoid.");

            var report = SampleAnimator(cloneAnimator, clip, sampleRate);
            string fullPath = ResolveOutputPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllText(fullPath, JsonUtility.ToJson(report, true));
            Debug.Log("[HumanoidPoseAuditExporter] Wrote pose audit to " + fullPath);

            if (!string.IsNullOrWhiteSpace(avatarProfileOutputPath))
            {
                AvatarProfileReport avatarProfile = CreateAvatarProfile(report, cloneAnimator);
                string avatarProfileFullPath = ResolveOutputPath(avatarProfileOutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(avatarProfileFullPath) ?? ".");
                File.WriteAllText(avatarProfileFullPath, JsonUtility.ToJson(avatarProfile, true));
                Debug.Log("[HumanoidPoseAuditExporter] Wrote avatar profile to " + avatarProfileFullPath);
            }
        }
        finally
        {
            DestroyImmediate(clone);
        }
    }

    private static AvatarProfileReport CreateAvatarProfile(PoseAuditReport report, Animator animator)
    {
        var profile = new AvatarProfileReport
        {
            AvatarName = report.AvatarName,
            HumanScale = report.AvatarHumanScale,
            CalibrationClipName = report.ClipName,
            CalibrationRootMotionSettings = report.RootMotionSettings,
            RootAllocationFrame = CaptureRootAllocationFrame(animator),
            AvatarSettings = report.AvatarSettings,
        };

        foreach (BoneDefinition bone in BonesToSample)
        {
            Transform boneTransform = animator.GetBoneTransform(bone.Bone);
            if (boneTransform == null)
                continue;

            profile.AvatarRoles.Add(new AvatarProfileRole
            {
                HumanName = bone.Name,
                TransformName = boneTransform.name,
                Required = IsRequiredHumanoidRole(bone.Bone),
            });
        }

        AddTwistChain(
            profile,
            "LeftArm",
            "LeftUpperArm",
            "LeftLowerArm",
            "LeftHand",
            report.AvatarSettings.UpperArmTwist,
            report.AvatarSettings.LowerArmTwist);
        AddTwistChain(
            profile,
            "RightArm",
            "RightUpperArm",
            "RightLowerArm",
            "RightHand",
            report.AvatarSettings.UpperArmTwist,
            report.AvatarSettings.LowerArmTwist);
        AddTwistChain(
            profile,
            "LeftLeg",
            "LeftUpperLeg",
            "LeftLowerLeg",
            "LeftFoot",
            report.AvatarSettings.UpperLegTwist,
            report.AvatarSettings.LowerLegTwist);
        AddTwistChain(
            profile,
            "RightLeg",
            "RightUpperLeg",
            "RightLowerLeg",
            "RightFoot",
            report.AvatarSettings.UpperLegTwist,
            report.AvatarSettings.LowerLegTwist);

        if (report.DefaultMusclePose != null)
        {
            foreach (BoneSample bone in report.DefaultMusclePose.Bones)
            {
                profile.NeutralPoseBoneRotations.Add(new AvatarProfileNeutralBone
                {
                    Name = bone.Name,
                    LocalPosition = bone.LocalPosition,
                    BindRelativeRotation = bone.BindRelativeRotation,
                });
            }
        }

        foreach (MuscleProbe probe in report.MuscleProbes)
        {
            foreach (MuscleProbeBone bone in probe.Bones)
            {
                profile.MuscleResponses.Add(new AvatarProfileMuscleResponse
                {
                    MuscleIndex = probe.Index,
                    MuscleName = probe.Name,
                    BoneName = bone.Name,
                    NegativePoseDeltaFromNeutralRotation = bone.NegativePoseDeltaFromNeutralRotation,
                    PositivePoseDeltaFromNeutralRotation = bone.PositivePoseDeltaFromNeutralRotation,
                });
            }
        }

        profile.CoupledMuscleCalibrations = CaptureCoupledMuscleCalibrations(
            animator,
            report);

        return profile;
    }

    private static AvatarProfileRootAllocationFrame CaptureRootAllocationFrame(Animator animator)
    {
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform hipsParent = hips != null ? hips.parent : null;
        if (hipsParent == null)
            return new AvatarProfileRootAllocationFrame();

        Matrix4x4 parentToAnimatorRoot = animator.transform.worldToLocalMatrix
            * hipsParent.localToWorldMatrix;
        return new AvatarProfileRootAllocationFrame
        {
            HipsParentPositionInAnimatorRoot = PoseVector3.From(parentToAnimatorRoot.GetColumn(3)),
            HipsParentRotationInAnimatorRoot = PoseQuaternion.From(parentToAnimatorRoot.rotation),
            HipsParentScaleInAnimatorRoot = PoseVector3.From(parentToAnimatorRoot.lossyScale),
        };
    }

    private static void AddTwistChain(
        AvatarProfileReport profile,
        string name,
        string proximalRole,
        string distalRole,
        string endRole,
        float proximalDistribution,
        float distalDistribution)
    {
        profile.TwistChains.Add(new AvatarProfileTwistChain
        {
            Name = name,
            ProximalRole = proximalRole,
            DistalRole = distalRole,
            EndRole = endRole,
            ProximalDistribution = proximalDistribution,
            DistalDistribution = distalDistribution,
        });
    }

    private static bool IsRequiredHumanoidRole(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.Hips:
            case HumanBodyBones.Spine:
            case HumanBodyBones.Head:
            case HumanBodyBones.LeftUpperArm:
            case HumanBodyBones.LeftLowerArm:
            case HumanBodyBones.LeftHand:
            case HumanBodyBones.RightUpperArm:
            case HumanBodyBones.RightLowerArm:
            case HumanBodyBones.RightHand:
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.LeftLowerLeg:
            case HumanBodyBones.LeftFoot:
            case HumanBodyBones.RightUpperLeg:
            case HumanBodyBones.RightLowerLeg:
            case HumanBodyBones.RightFoot:
                return true;
            default:
                return false;
        }
    }

    private static PoseAuditReport SampleAnimator(Animator animator, AnimationClip clip, int sampleRate)
    {
        var report = new PoseAuditReport
        {
            ClipName = clip.name,
            AvatarName = animator.gameObject.name,
            DurationSeconds = clip.length,
            SampleRate = sampleRate,
            AvatarHumanScale = animator.humanScale,
            AnimatorApplyRootMotion = animator.applyRootMotion,
            AvatarSettings = ReadAvatarSettings(animator.avatar),
            RootMotionSettings = ReadRootMotionSettings(clip),
        };

        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        PopulateMuscleDefaultRanges(report);
        List<RawCurveBinding> rawCurveBindings = CollectRawCurveBindings(clip);
        Dictionary<HumanBodyBones, Quaternion> bindLocalRotations = CaptureBindLocalRotations(animator);
        report.DefaultMusclePose = CaptureDefaultMusclePose(animator, bindLocalRotations);
        Dictionary<string, Quaternion> neutralBindRelativeRotations =
            CaptureNeutralBindRelativeRotations(report.DefaultMusclePose);
        report.MuscleProbes = CaptureMuscleProbes(animator);

        int sampleCount = Mathf.Max(1, Mathf.CeilToInt(clip.length * sampleRate) + 1);
        report.SampleCount = sampleCount;

        PlayableGraph graph = PlayableGraph.Create("HumanoidPoseAudit");
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

            var output = AnimationPlayableOutput.Create(graph, "Animation", animator);
            var playable = AnimationClipPlayable.Create(graph, clip);
            playable.SetApplyFootIK(false);
            playable.SetApplyPlayableIK(false);
            playable.SetSpeed(0.0);
            output.SetSourcePlayable(playable);
            graph.Play();

            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var humanPose = new HumanPose();

            for (int i = 0; i < sampleCount; i++)
            {
                float sampleTime = sampleCount == 1
                    ? 0.0f
                    : Mathf.Min(i / (float)sampleRate, clip.length);

                playable.SetTime(sampleTime);
                playable.SetDone(false);
                graph.Evaluate(0.0f);
                poseHandler.GetHumanPose(ref humanPose);

                report.Samples.Add(CaptureSample(
                    animator,
                    humanPose,
                    sampleTime,
                    i,
                    rawCurveBindings,
                    bindLocalRotations,
                    neutralBindRelativeRotations));
            }
        }
        finally
        {
            graph.Destroy();
        }

        return report;
    }

    private static Dictionary<HumanBodyBones, Quaternion> CaptureBindLocalRotations(Animator animator)
    {
        var bindLocalRotations = new Dictionary<HumanBodyBones, Quaternion>();
        foreach (BoneDefinition bone in BonesToSample)
        {
            Transform boneTransform = animator.GetBoneTransform(bone.Bone);
            if (boneTransform == null)
                continue;

            bindLocalRotations[bone.Bone] = Quaternion.Normalize(boneTransform.localRotation);
        }

        return bindLocalRotations;
    }

    private static PoseAuditSample CaptureDefaultMusclePose(
        Animator sourceAnimator,
        IReadOnlyDictionary<HumanBodyBones, Quaternion> bindLocalRotations)
    {
        GameObject clone = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
        clone.hideFlags = HideFlags.HideAndDontSave;
        clone.name = sourceAnimator.gameObject.name + "_DefaultPoseAuditClone";
        try
        {
            DisableBehaviours(clone);
            Animator animator = clone.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
                throw new InvalidOperationException("Default-pose clone is missing a humanoid Animator.");

            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var defaultPose = new HumanPose
            {
                bodyPosition = Vector3.zero,
                bodyRotation = Quaternion.identity,
                muscles = new float[HumanTrait.MuscleCount],
            };

            poseHandler.SetHumanPose(ref defaultPose);
            poseHandler.GetHumanPose(ref defaultPose);
            return CaptureSample(
                animator,
                defaultPose,
                0.0f,
                -1,
                Array.Empty<RawCurveBinding>(),
                bindLocalRotations,
                null);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(clone);
        }
    }

    private static PoseAuditSample CaptureSample(
        Animator animator,
        HumanPose humanPose,
        float timeSeconds,
        int index,
        IReadOnlyList<RawCurveBinding> rawCurveBindings,
        IReadOnlyDictionary<HumanBodyBones, Quaternion> bindLocalRotations,
        IReadOnlyDictionary<string, Quaternion> neutralBindRelativeRotations)
    {
        var sample = new PoseAuditSample
        {
            Index = index,
            TimeSeconds = timeSeconds,
            BodyPosition = PoseVector3.From(humanPose.bodyPosition),
            BodyRotation = PoseQuaternion.From(humanPose.bodyRotation),
            CharacterRootLocalPosition = PoseVector3.From(animator.transform.localPosition),
            CharacterRootLocalRotation = PoseQuaternion.From(animator.transform.localRotation),
            CharacterRootWorldPosition = PoseVector3.From(animator.transform.position),
            CharacterRootWorldRotation = PoseQuaternion.From(animator.transform.rotation),
            ProjectedRootPosition = PoseVector3.From(animator.rootPosition),
            ProjectedRootRotation = PoseQuaternion.From(animator.rootRotation),
            RootMotionDeltaPosition = PoseVector3.From(animator.deltaPosition),
            RootMotionDeltaRotation = PoseQuaternion.From(animator.deltaRotation),
            LeftFeetBottomHeight = animator.leftFeetBottomHeight,
            RightFeetBottomHeight = animator.rightFeetBottomHeight,
        };

        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips != null)
        {
            sample.HipsLocalPosition = PoseVector3.From(hips.localPosition);
            sample.HipsLocalRotation = PoseQuaternion.From(hips.localRotation);
        }

        string[] muscleNames = HumanTrait.MuscleName;
        int muscleCount = Mathf.Min(muscleNames.Length, humanPose.muscles != null ? humanPose.muscles.Length : 0);
        for (int i = 0; i < muscleCount; i++)
        {
            sample.Muscles.Add(new NamedFloat
            {
                Name = muscleNames[i],
                Value = humanPose.muscles[i],
            });
        }

        foreach (RawCurveBinding rawCurve in rawCurveBindings)
        {
            sample.RawCurves.Add(new RawCurveSample
            {
                Path = rawCurve.Path,
                TypeName = rawCurve.TypeName,
                PropertyName = rawCurve.PropertyName,
                Value = rawCurve.Curve.Evaluate(timeSeconds),
            });
        }

        Transform root = animator.transform;
        foreach (BoneDefinition bone in BonesToSample)
        {
            Transform boneTransform = animator.GetBoneTransform(bone.Bone);
            if (boneTransform == null)
                continue;

            Quaternion bindLocalRotation = bindLocalRotations.TryGetValue(bone.Bone, out Quaternion capturedBindLocal)
                ? capturedBindLocal
                : Quaternion.identity;
            Quaternion bindRelativeRotation = Quaternion.Normalize(
                Quaternion.Inverse(bindLocalRotation) * boneTransform.localRotation);
            Quaternion neutralBindRelativeRotation =
                neutralBindRelativeRotations != null &&
                neutralBindRelativeRotations.TryGetValue(bone.Name, out Quaternion capturedNeutral)
                    ? capturedNeutral
                    : bindRelativeRotation;

            sample.Bones.Add(new BoneSample
            {
                Name = bone.Name,
                LocalPosition = PoseVector3.From(boneTransform.localPosition),
                LocalRotation = PoseQuaternion.From(boneTransform.localRotation),
                BindRelativeRotation = PoseQuaternion.From(bindRelativeRotation),
                NeutralBindRelativeRotation = PoseQuaternion.From(neutralBindRelativeRotation),
                PoseDeltaFromNeutralRotation = PoseQuaternion.From(
                    Quaternion.Inverse(neutralBindRelativeRotation) * bindRelativeRotation),
                RootSpacePosition = PoseVector3.From(root.InverseTransformPoint(boneTransform.position)),
                WorldPosition = PoseVector3.From(boneTransform.position),
            });
        }

        return sample;
    }

    private static Dictionary<string, Quaternion> CaptureNeutralBindRelativeRotations(PoseAuditSample defaultPose)
    {
        var result = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
        for (int i = 0; i < defaultPose.Bones.Count; i++)
        {
            BoneSample bone = defaultPose.Bones[i];
            result[bone.Name] = bone.BindRelativeRotation.ToQuaternion();
        }

        return result;
    }

    private static List<MuscleProbe> CaptureMuscleProbes(Animator sourceAnimator)
    {
        GameObject clone = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
        clone.hideFlags = HideFlags.HideAndDontSave;
        clone.name = sourceAnimator.gameObject.name + "_MuscleProbeClone";
        try
        {
            DisableBehaviours(clone);
            Animator animator = clone.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
                throw new InvalidOperationException("Muscle-probe clone is missing a humanoid Animator.");

            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var neutralPose = new HumanPose
            {
                bodyPosition = Vector3.zero,
                bodyRotation = Quaternion.identity,
                muscles = new float[HumanTrait.MuscleCount],
            };
            poseHandler.SetHumanPose(ref neutralPose);

            var neutralLocalRotations = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
            foreach (BoneDefinition bone in BonesToSample)
            {
                Transform boneTransform = animator.GetBoneTransform(bone.Bone);
                if (boneTransform != null)
                    neutralLocalRotations[bone.Name] = Quaternion.Normalize(boneTransform.localRotation);
            }

            string[] muscleNames = HumanTrait.MuscleName;
            int muscleCount = Mathf.Min(muscleNames.Length, HumanTrait.MuscleCount);
            var result = new List<MuscleProbe>(muscleCount);
            for (int muscleIndex = 0; muscleIndex < muscleCount; muscleIndex++)
            {
                Dictionary<string, Quaternion> negative = CaptureMuscleProbePose(
                    poseHandler,
                    animator,
                    muscleIndex,
                    -1.0f,
                    neutralLocalRotations);
                Dictionary<string, Quaternion> positive = CaptureMuscleProbePose(
                    poseHandler,
                    animator,
                    muscleIndex,
                    1.0f,
                    neutralLocalRotations);

                var probe = new MuscleProbe
                {
                    Index = muscleIndex,
                    Name = muscleNames[muscleIndex],
                };

                foreach (BoneDefinition bone in BonesToSample)
                {
                    Quaternion negativeDelta = negative.TryGetValue(bone.Name, out Quaternion capturedNegative)
                        ? capturedNegative
                        : Quaternion.identity;
                    Quaternion positiveDelta = positive.TryGetValue(bone.Name, out Quaternion capturedPositive)
                        ? capturedPositive
                        : Quaternion.identity;
                    if (Quaternion.Angle(Quaternion.identity, negativeDelta) <= 0.001f &&
                        Quaternion.Angle(Quaternion.identity, positiveDelta) <= 0.001f)
                    {
                        continue;
                    }

                    probe.Bones.Add(new MuscleProbeBone
                    {
                        Name = bone.Name,
                        NegativePoseDeltaFromNeutralRotation = PoseQuaternion.From(negativeDelta),
                        PositivePoseDeltaFromNeutralRotation = PoseQuaternion.From(positiveDelta),
                    });
                }

                result.Add(probe);
            }

            poseHandler.SetHumanPose(ref neutralPose);
            return result;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(clone);
        }
    }

    private static Dictionary<string, Quaternion> CaptureMuscleProbePose(
        HumanPoseHandler poseHandler,
        Animator animator,
        int muscleIndex,
        float muscleValue,
        IReadOnlyDictionary<string, Quaternion> neutralLocalRotations)
    {
        var pose = new HumanPose
        {
            bodyPosition = Vector3.zero,
            bodyRotation = Quaternion.identity,
            muscles = new float[HumanTrait.MuscleCount],
        };
        pose.muscles[muscleIndex] = muscleValue;
        poseHandler.SetHumanPose(ref pose);

        var result = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
        foreach (BoneDefinition bone in BonesToSample)
        {
            Transform boneTransform = animator.GetBoneTransform(bone.Bone);
            if (boneTransform == null || !neutralLocalRotations.TryGetValue(bone.Name, out Quaternion neutralRotation))
                continue;

            result[bone.Name] = Quaternion.Normalize(
                Quaternion.Inverse(neutralRotation) * boneTransform.localRotation);
        }

        return result;
    }

    /// <summary>
    /// Captures deterministic multi-muscle poses independently for each bone.
    /// Unrelated muscles must remain zero: Mecanim can redistribute a full-body
    /// HumanPose even when an isolated probe does not move a given local bone,
    /// and treating those values as hidden inputs makes a per-bone fit noisy.
    /// </summary>
    private static List<AvatarProfileCoupledMuscleCalibration> CaptureCoupledMuscleCalibrations(
        Animator sourceAnimator,
        PoseAuditReport report)
    {
        IReadOnlyList<MuscleProbe> muscleProbes = report.MuscleProbes;
        Dictionary<string, List<int>> boneMuscles = CollectInfluencingMuscles(muscleProbes);
        if (boneMuscles.Count == 0)
            return new List<AvatarProfileCoupledMuscleCalibration>();

        string[] muscleNames = HumanTrait.MuscleName;
        var result = new List<AvatarProfileCoupledMuscleCalibration>(boneMuscles.Count);
        foreach (BoneDefinition bone in BonesToSample)
        {
            if (!boneMuscles.TryGetValue(bone.Name, out List<int> muscles) || muscles.Count == 0)
                continue;

            // Twice the feature count gives useful overdetermination while the
            // cap prevents Hips calibration from becoming an unbounded tool job.
            int combinationSampleCount = Mathf.Clamp(
                Mathf.Max(64, GetFeatureCount(muscles.Count) * 2),
                64,
                1280);
            List<CoupledCalibrationSample> samples = CaptureCoupledCalibrationSamples(
                sourceAnimator,
                muscles,
                combinationSampleCount,
                useCentralAmplitudeBands: string.Equals(bone.Name, "Hips", StringComparison.Ordinal));
            // Hips is Mecanim's coupled body-frame output and is demonstrably
            // affected by torso/shoulder combinations. Ordinary limb-local models
            // remain avatar-generic; feeding their targets from a full-body clip
            // would incorrectly attribute unrelated whole-body effects to three
            // local muscle inputs.
            int referenceMotionSampleCount = string.Equals(bone.Name, "Hips", StringComparison.Ordinal)
                ? AppendReferenceMotionSamples(samples, sourceAnimator, report, muscles)
                : 0;

            result.Add(FitCoupledMuscleCalibration(
                bone.Name,
                muscles,
                muscleNames,
                muscleProbes,
                samples,
                combinationSampleCount,
                muscles.Count * 2,
                referenceMotionSampleCount));
        }

        return result;
    }

    private static Dictionary<string, List<int>> CollectInfluencingMuscles(IReadOnlyList<MuscleProbe> muscleProbes)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int probeIndex = 0; probeIndex < muscleProbes.Count; probeIndex++)
        {
            MuscleProbe probe = muscleProbes[probeIndex];
            for (int boneIndex = 0; boneIndex < probe.Bones.Count; boneIndex++)
            {
                MuscleProbeBone bone = probe.Bones[boneIndex];
                if (!result.TryGetValue(bone.Name, out List<int> muscles))
                {
                    muscles = new List<int>();
                    result.Add(bone.Name, muscles);
                }

                if (!muscles.Contains(probe.Index))
                    muscles.Add(probe.Index);
            }
        }

        foreach (KeyValuePair<string, List<int>> entry in result)
            entry.Value.Sort();

        return result;
    }

    private static List<CoupledCalibrationSample> CaptureCoupledCalibrationSamples(
        Animator sourceAnimator,
        IReadOnlyList<int> unionMuscles,
        int combinationSampleCount,
        bool useCentralAmplitudeBands)
    {
        GameObject clone = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
        clone.hideFlags = HideFlags.HideAndDontSave;
        clone.name = sourceAnimator.gameObject.name + "_CoupledMuscleProbeClone";
        try
        {
            DisableBehaviours(clone);
            Animator animator = clone.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
                throw new InvalidOperationException("Coupled-muscle probe clone is missing a humanoid Animator.");

            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var neutralPose = new HumanPose
            {
                bodyPosition = Vector3.zero,
                bodyRotation = Quaternion.identity,
                muscles = new float[HumanTrait.MuscleCount],
            };
            poseHandler.SetHumanPose(ref neutralPose);

            Dictionary<string, Quaternion> neutralLocalRotations = CaptureCurrentLocalRotations(animator);
            Dictionary<string, Vector3> neutralLocalPositions = CaptureCurrentLocalPositions(animator);
            var inputs = new List<float[]>(unionMuscles.Count * 2 + combinationSampleCount);
            for (int i = 0; i < unionMuscles.Count; i++)
            {
                inputs.Add(CreateSingleMuscleInput(unionMuscles[i], -1.0f));
                inputs.Add(CreateSingleMuscleInput(unionMuscles[i], 1.0f));
            }

            for (int sampleIndex = 1; sampleIndex <= combinationSampleCount; sampleIndex++)
                inputs.Add(CreateLowDiscrepancyInput(unionMuscles, sampleIndex, useCentralAmplitudeBands));

            var result = new List<CoupledCalibrationSample>(inputs.Count);
            for (int i = 0; i < inputs.Count; i++)
                result.Add(CaptureCoupledCalibrationSample(
                    poseHandler,
                    animator,
                    inputs[i],
                    neutralLocalRotations,
                    neutralLocalPositions));

            poseHandler.SetHumanPose(ref neutralPose);
            return result;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(clone);
        }
    }

    private static Dictionary<string, Quaternion> CaptureCurrentLocalRotations(Animator animator)
    {
        var result = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
        foreach (BoneDefinition bone in BonesToSample)
        {
            Transform transform = animator.GetBoneTransform(bone.Bone);
            if (transform != null)
                result[bone.Name] = Quaternion.Normalize(transform.localRotation);
        }

        return result;
    }

    private static Dictionary<string, Vector3> CaptureCurrentLocalPositions(Animator animator)
    {
        var result = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (BoneDefinition bone in BonesToSample)
        {
            Transform transform = animator.GetBoneTransform(bone.Bone);
            if (transform != null)
                result[bone.Name] = transform.localPosition;
        }

        return result;
    }

    private static float[] CreateSingleMuscleInput(int muscleIndex, float value)
    {
        var result = new float[HumanTrait.MuscleCount];
        result[muscleIndex] = value;
        return result;
    }

    private static float[] CreateLowDiscrepancyInput(
        IReadOnlyList<int> muscleIndices,
        int sampleIndex,
        bool useCentralAmplitudeBands)
    {
        var result = new float[HumanTrait.MuscleCount];
        int scaleBand = useCentralAmplitudeBands ? (sampleIndex - 1) % 3 : 0;
        int sequenceIndex = sampleIndex;
        float amplitude = !useCentralAmplitudeBands || scaleBand == 0
            ? 1.0f
            : scaleBand == 1 ? 0.6f : 0.3f;
        for (int i = 0; i < muscleIndices.Count; i++)
        {
            // A prime-per-dimension Halton sequence is deterministic and avoids
            // correlated grids without relying on UnityEngine.Random state. Every
            // sample keeps its own direction: reusing one direction for all three
            // amplitude bands leaves the cubic Hips basis rank-deficient. The bands
            // still preserve full-range coverage while emphasizing ordinary poses.
            float unit = RadicalInverse(sequenceIndex, GetPrime(i));
            result[muscleIndices[i]] = (unit * 2.0f - 1.0f) * amplitude;
        }

        return result;
    }

    private static int GetPrime(int index)
    {
        if (index < HaltonPrimes.Length)
            return HaltonPrimes[index];

        return HaltonPrimes[HaltonPrimes.Length - 1] + (index - HaltonPrimes.Length + 1) * 2;
    }

    private static float RadicalInverse(int index, int radix)
    {
        float inverseRadix = 1.0f / radix;
        float factor = inverseRadix;
        float result = 0.0f;
        while (index > 0)
        {
            result += (index % radix) * factor;
            index /= radix;
            factor *= inverseRadix;
        }

        return result;
    }

    private static CoupledCalibrationSample CaptureCoupledCalibrationSample(
        HumanPoseHandler poseHandler,
        Animator animator,
        float[] muscles,
        IReadOnlyDictionary<string, Quaternion> neutralLocalRotations,
        IReadOnlyDictionary<string, Vector3> neutralLocalPositions)
    {
        var pose = new HumanPose
        {
            bodyPosition = Vector3.zero,
            bodyRotation = Quaternion.identity,
            muscles = muscles,
        };
        poseHandler.SetHumanPose(ref pose);
        var result = new CoupledCalibrationSample { Muscles = muscles };
        foreach (BoneDefinition bone in BonesToSample)
        {
            Transform transform = animator.GetBoneTransform(bone.Bone);
            if (transform == null || !neutralLocalRotations.TryGetValue(bone.Name, out Quaternion neutralRotation))
                continue;

            result.BonePoseDeltas[bone.Name] = Quaternion.Normalize(
                Quaternion.Inverse(neutralRotation) * transform.localRotation);
            if (neutralLocalPositions.TryGetValue(bone.Name, out Vector3 neutralPosition))
                result.BonePositionDeltas[bone.Name] = transform.localPosition - neutralPosition;
        }

        return result;
    }

    private static AvatarProfileCoupledMuscleCalibration FitCoupledMuscleCalibration(
        string boneName,
        IReadOnlyList<int> muscleIndices,
        string[] muscleNames,
        IReadOnlyList<MuscleProbe> muscleProbes,
        IReadOnlyList<CoupledCalibrationSample> samples,
        int combinationSampleCount,
        int endpointSampleCount,
        int referenceMotionSampleCount)
    {
        const float RidgeLambda = 0.0001f;
        int featureCount = GetFeatureCount(muscleIndices.Count);
        var calibration = new AvatarProfileCoupledMuscleCalibration
        {
            BoneName = boneName,
            MaximumPolynomialDegree = GetMaximumPolynomialDegree(muscleIndices.Count),
            FeatureCount = featureCount,
            SampleCount = samples.Count,
            CombinationSampleCount = combinationSampleCount,
            EndpointSampleCount = endpointSampleCount,
            ReferenceMotionSampleCount = referenceMotionSampleCount,
            RidgeLambda = RidgeLambda,
        };
        for (int i = 0; i < muscleIndices.Count; i++)
        {
            int muscleIndex = muscleIndices[i];
            calibration.MuscleIndices.Add(muscleIndex);
            calibration.MuscleNames.Add(
                muscleIndex >= 0 && muscleIndex < muscleNames.Length ? muscleNames[muscleIndex] : string.Empty);

            if (!TryGetProbeEndpoints(
                    muscleProbes,
                    boneName,
                    muscleIndex,
                    out Quaternion negativeRotation,
                    out Quaternion positiveRotation))
            {
                negativeRotation = Quaternion.identity;
                positiveRotation = Quaternion.identity;
            }

            calibration.NegativeEndpointRotations.Add(PoseQuaternion.From(negativeRotation));
            calibration.PositiveEndpointRotations.Add(PoseQuaternion.From(positiveRotation));
            calibration.NegativeEndpointPositionDeltas.Add(PoseVector3.From(
                FindEndpointPositionDelta(samples, boneName, muscleIndex, -1.0f)));
            calibration.PositiveEndpointPositionDeltas.Add(PoseVector3.From(
                FindEndpointPositionDelta(samples, boneName, muscleIndex, 1.0f)));
        }

        var normal = new double[featureCount, featureCount];
        var rotationX = new double[featureCount];
        var rotationY = new double[featureCount];
        var rotationZ = new double[featureCount];
        var positionX = new double[featureCount];
        var positionY = new double[featureCount];
        var positionZ = new double[featureCount];
        var features = new List<float[]>(samples.Count);
        var rotationTargets = new List<Vector3>(samples.Count);
        var positionTargets = new List<Vector3>(samples.Count);

        for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            CoupledCalibrationSample sample = samples[sampleIndex];
            if (!sample.BonePoseDeltas.TryGetValue(boneName, out Quaternion rotation) ||
                !sample.BonePositionDeltas.TryGetValue(boneName, out Vector3 position))
            {
                continue;
            }

            float[] featureVector = CreateFeatureVector(sample.Muscles, muscleIndices);
            Quaternion baselineRotation = EvaluateEndpointRotationBaseline(
                sample.Muscles,
                muscleIndices,
                calibration);
            Vector3 baselinePosition = EvaluateEndpointPositionBaseline(
                sample.Muscles,
                muscleIndices,
                calibration);
            Quaternion residualRotation = Quaternion.Normalize(
                Quaternion.Inverse(baselineRotation) * rotation);
            Vector3 rotationVector = ToShortestRotationVector(residualRotation);
            Vector3 residualPosition = position - baselinePosition;
            features.Add(featureVector);
            rotationTargets.Add(rotationVector);
            positionTargets.Add(residualPosition);
            AccumulateNormalEquation(
                normal,
                featureVector,
                rotationVector,
                residualPosition,
                sample.Weight,
                rotationX,
                rotationY,
                rotationZ,
                positionX,
                positionY,
                positionZ);
        }

        AddRidge(normal, RidgeLambda);
        double[][] solved = SolveLinearSystems(
            normal,
            rotationX,
            rotationY,
            rotationZ,
            positionX,
            positionY,
            positionZ);
        double[] solvedRotationX = solved[0];
        double[] solvedRotationY = solved[1];
        double[] solvedRotationZ = solved[2];
        double[] solvedPositionX = solved[3];
        double[] solvedPositionY = solved[4];
        double[] solvedPositionZ = solved[5];
        CopyCoefficients(solvedRotationX, calibration.XCoefficients);
        CopyCoefficients(solvedRotationY, calibration.YCoefficients);
        CopyCoefficients(solvedRotationZ, calibration.ZCoefficients);
        CopyCoefficients(solvedPositionX, calibration.PositionXCoefficients);
        CopyCoefficients(solvedPositionY, calibration.PositionYCoefficients);
        CopyCoefficients(solvedPositionZ, calibration.PositionZCoefficients);
        CalculateFitErrors(
            features,
            rotationTargets,
            positionTargets,
            solvedRotationX,
            solvedRotationY,
            solvedRotationZ,
            solvedPositionX,
            solvedPositionY,
            solvedPositionZ,
            calibration);
        FitProjectedRootY(samples, muscleIndices, calibration);
        return calibration;
    }

    /// <summary>
    /// Adds every other real-motion muscle vector to the avatar-generic calibration
    /// set after replaying it with a neutral Body pose. Sampling the final clip Hips
    /// transform here would bake that clip's Body/root allocation into the muscle
    /// model and make the profile fail on unrelated motions.
    /// </summary>
    private static int AppendReferenceMotionSamples(
        List<CoupledCalibrationSample> destination,
        Animator sourceAnimator,
        PoseAuditReport report,
        IReadOnlyList<int> muscleIndices)
    {
        if (report.DefaultMusclePose == null || report.Samples.Count == 0)
            return 0;

        GameObject clone = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
        clone.hideFlags = HideFlags.HideAndDontSave;
        clone.name = sourceAnimator.gameObject.name + "_ReferenceMuscleProbeClone";
        try
        {
            DisableBehaviours(clone);
            Animator animator = clone.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
                throw new InvalidOperationException("Reference-muscle probe clone is missing a humanoid Animator.");

            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var neutralPose = new HumanPose
            {
                bodyPosition = Vector3.zero,
                bodyRotation = Quaternion.identity,
                muscles = new float[HumanTrait.MuscleCount],
            };
            poseHandler.SetHumanPose(ref neutralPose);
            Dictionary<string, Quaternion> neutralLocalRotations = CaptureCurrentLocalRotations(animator);
            Dictionary<string, Vector3> neutralLocalPositions = CaptureCurrentLocalPositions(animator);

            int added = 0;
            for (int sampleIndex = 0; sampleIndex < report.Samples.Count; sampleIndex += 2)
            {
                PoseAuditSample source = report.Samples[sampleIndex];
                var muscleValuesByName = new Dictionary<string, float>(source.Muscles.Count, StringComparer.Ordinal);
                for (int i = 0; i < source.Muscles.Count; i++)
                    muscleValuesByName[source.Muscles[i].Name] = source.Muscles[i].Value;

                var muscles = new float[HumanTrait.MuscleCount];
                string[] humanTraitMuscleNames = HumanTrait.MuscleName;
                for (int i = 0; i < muscleIndices.Count; i++)
                {
                    int muscleIndex = muscleIndices[i];
                    if (muscleIndex >= 0
                        && muscleIndex < humanTraitMuscleNames.Length
                        && muscleValuesByName.TryGetValue(humanTraitMuscleNames[muscleIndex], out float value))
                        muscles[muscleIndex] = value;
                }

                CoupledCalibrationSample sample = CaptureCoupledCalibrationSample(
                    poseHandler,
                    animator,
                    muscles,
                    neutralLocalRotations,
                    neutralLocalPositions);
                // Real animation samples improve coverage of ordinary poses, but
                // they must not dominate the avatar-wide Halton calibration set.
                // A clip-heavy weight overfits Hips to the calibration motion and
                // makes the same avatar profile fail on unrelated clips.
                sample.Weight = 1.0f;
                sample.HasProjectedRootY = true;
                sample.ProjectedRootY = source.ProjectedRootPosition.Y;
                destination.Add(sample);
                added++;
            }

            poseHandler.SetHumanPose(ref neutralPose);
            return added;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(clone);
        }
    }

    private static void FitProjectedRootY(
        IReadOnlyList<CoupledCalibrationSample> samples,
        IReadOnlyList<int> muscleIndices,
        AvatarProfileCoupledMuscleCalibration calibration)
    {
        int featureCount = calibration.FeatureCount;
        var normal = new double[featureCount, featureCount];
        var target = new double[featureCount];
        var featureVectors = new List<float[]>();
        var targets = new List<float>();
        for (int i = 0; i < samples.Count; i++)
        {
            CoupledCalibrationSample sample = samples[i];
            if (!sample.HasProjectedRootY)
                continue;

            float[] features = CreateFeatureVector(sample.Muscles, muscleIndices);
            featureVectors.Add(features);
            targets.Add(sample.ProjectedRootY);
            for (int row = 0; row < features.Length; row++)
            {
                double value = features[row];
                target[row] += value * sample.ProjectedRootY;
                for (int column = 0; column < features.Length; column++)
                    normal[row, column] += value * features[column];
            }
        }

        calibration.ProjectedRootYTrainingSampleCount = featureVectors.Count;
        if (featureVectors.Count == 0)
            return;

        AddRidge(normal, 0.0001f);
        double[] coefficients = SolveLinearSystems(normal, target)[0];
        CopyCoefficients(coefficients, calibration.ProjectedRootYCoefficients);
        calibration.ProjectedRootYZeroOffset = EvaluateScalar(featureVectors[0], coefficients);
        float errorSum = 0.0f;
        for (int i = 0; i < featureVectors.Count; i++)
        {
            float predicted = EvaluateScalar(featureVectors[i], coefficients);
            float error = Mathf.Abs(predicted - targets[i]);
            errorSum += error;
            calibration.MaxProjectedRootYError = Mathf.Max(calibration.MaxProjectedRootYError, error);
        }

        calibration.MeanProjectedRootYError = errorSum / featureVectors.Count;
    }

    private static float EvaluateScalar(float[] features, double[] coefficients)
    {
        float result = 0.0f;
        for (int i = 0; i < features.Length; i++)
            result += features[i] * (float)coefficients[i];
        return result;
    }

    private static bool TryGetProbeEndpoints(
        IReadOnlyList<MuscleProbe> muscleProbes,
        string boneName,
        int muscleIndex,
        out Quaternion negative,
        out Quaternion positive)
    {
        if (muscleIndex >= 0 && muscleIndex < muscleProbes.Count)
        {
            MuscleProbe probe = muscleProbes[muscleIndex];
            for (int i = 0; i < probe.Bones.Count; i++)
            {
                MuscleProbeBone bone = probe.Bones[i];
                if (!string.Equals(bone.Name, boneName, StringComparison.Ordinal))
                    continue;

                negative = bone.NegativePoseDeltaFromNeutralRotation.ToQuaternion();
                positive = bone.PositivePoseDeltaFromNeutralRotation.ToQuaternion();
                return true;
            }
        }

        negative = Quaternion.identity;
        positive = Quaternion.identity;
        return false;
    }

    private static Vector3 FindEndpointPositionDelta(
        IReadOnlyList<CoupledCalibrationSample> samples,
        string boneName,
        int muscleIndex,
        float endpoint)
    {
        for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            CoupledCalibrationSample sample = samples[sampleIndex];
            if (!Mathf.Approximately(sample.Muscles[muscleIndex], endpoint))
                continue;

            bool isSingleMuscleSample = true;
            for (int i = 0; i < sample.Muscles.Length; i++)
            {
                if (i != muscleIndex && !Mathf.Approximately(sample.Muscles[i], 0.0f))
                {
                    isSingleMuscleSample = false;
                    break;
                }
            }

            if (isSingleMuscleSample && sample.BonePositionDeltas.TryGetValue(boneName, out Vector3 position))
                return position;
        }

        return Vector3.zero;
    }

    private static Quaternion EvaluateEndpointRotationBaseline(
        float[] muscles,
        IReadOnlyList<int> muscleIndices,
        AvatarProfileCoupledMuscleCalibration calibration)
    {
        Quaternion result = Quaternion.identity;
        for (int i = 0; i < muscleIndices.Count; i++)
        {
            float amount = muscles[muscleIndices[i]];
            if (Mathf.Abs(amount) <= 1e-7f)
                continue;

            Quaternion endpoint = amount >= 0.0f
                ? calibration.PositiveEndpointRotations[i].ToQuaternion()
                : calibration.NegativeEndpointRotations[i].ToQuaternion();
            result = Quaternion.Normalize(result * ScaleShortestRotation(endpoint, Mathf.Abs(amount)));
        }

        return result;
    }

    private static Vector3 EvaluateEndpointPositionBaseline(
        float[] muscles,
        IReadOnlyList<int> muscleIndices,
        AvatarProfileCoupledMuscleCalibration calibration)
    {
        Vector3 result = Vector3.zero;
        for (int i = 0; i < muscleIndices.Count; i++)
        {
            float amount = muscles[muscleIndices[i]];
            PoseVector3 endpoint = amount >= 0.0f
                ? calibration.PositiveEndpointPositionDeltas[i]
                : calibration.NegativeEndpointPositionDeltas[i];
            result += new Vector3(endpoint.X, endpoint.Y, endpoint.Z) * Mathf.Abs(amount);
        }

        return result;
    }

    private static Quaternion ScaleShortestRotation(Quaternion rotation, float factor)
    {
        rotation = Quaternion.Normalize(rotation);
        if (rotation.w < 0.0f)
            rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);

        float halfAngle = Mathf.Acos(Mathf.Clamp(rotation.w, -1.0f, 1.0f));
        float sinHalfAngle = Mathf.Sin(halfAngle);
        if (Mathf.Abs(sinHalfAngle) <= 1e-7f)
            return Quaternion.identity;

        float scaledHalfAngle = halfAngle * factor;
        float axisScale = Mathf.Sin(scaledHalfAngle) / sinHalfAngle;
        return Quaternion.Normalize(new Quaternion(
            rotation.x * axisScale,
            rotation.y * axisScale,
            rotation.z * axisScale,
            Mathf.Cos(scaledHalfAngle)));
    }

    private static int GetFeatureCount(int muscleCount)
    {
        int count = muscleCount * 3 + muscleCount * (muscleCount - 1) / 2;
        int maximumDegree = GetMaximumPolynomialDegree(muscleCount);
        for (int degree = 3; degree <= maximumDegree; degree++)
            count += CombinationWithRepetitionCount(muscleCount, degree);
        return count;
    }

    private static int GetMaximumPolynomialDegree(int muscleCount)
    {
        if (muscleCount <= 3)
            return 5;
        if (muscleCount <= 6)
            return 4;
        return 3;
    }

    private static int CombinationWithRepetitionCount(int valueCount, int selectionCount)
    {
        long numerator = 1;
        long denominator = 1;
        for (int i = 1; i <= selectionCount; i++)
        {
            numerator *= valueCount + i - 1;
            denominator *= i;
        }
        return (int)(numerator / denominator);
    }

    private static float[] CreateFeatureVector(float[] muscles, IReadOnlyList<int> muscleIndices)
    {
        int muscleCount = muscleIndices.Count;
        var result = new float[GetFeatureCount(muscleCount)];
        var selectedValues = new float[muscleCount];
        for (int i = 0; i < muscleCount; i++)
            selectedValues[i] = muscles[muscleIndices[i]];

        int cursor = 0;
        for (int i = 0; i < muscleCount; i++)
            result[cursor++] = selectedValues[i];
        for (int i = 0; i < muscleCount; i++)
        {
            float value = selectedValues[i];
            result[cursor++] = value * value;
        }
        for (int i = 0; i < muscleCount; i++)
        {
            float value = selectedValues[i];
            result[cursor++] = value * Mathf.Abs(value);
        }
        for (int i = 0; i < muscleCount; i++)
        {
            float left = selectedValues[i];
            for (int j = i + 1; j < muscleCount; j++)
                result[cursor++] = left * selectedValues[j];
        }

        int maximumDegree = GetMaximumPolynomialDegree(muscleCount);
        for (int degree = 3; degree <= maximumDegree; degree++)
            AppendMonomials(selectedValues, degree, depth: 0, startIndex: 0, product: 1.0f, result, ref cursor);

        return result;
    }

    private static void AppendMonomials(
        float[] values,
        int degree,
        int depth,
        int startIndex,
        float product,
        float[] destination,
        ref int cursor)
    {
        if (depth == degree)
        {
            destination[cursor++] = product;
            return;
        }

        for (int i = startIndex; i < values.Length; i++)
            AppendMonomials(values, degree, depth + 1, i, product * values[i], destination, ref cursor);
    }

    private static void AccumulateNormalEquation(
        double[,] normal,
        float[] features,
        Vector3 rotation,
        Vector3 position,
        float sampleWeight,
        double[] rotationX,
        double[] rotationY,
        double[] rotationZ,
        double[] positionX,
        double[] positionY,
        double[] positionZ)
    {
        for (int row = 0; row < features.Length; row++)
        {
            double value = features[row];
            double weightedValue = value * sampleWeight;
            rotationX[row] += weightedValue * rotation.x;
            rotationY[row] += weightedValue * rotation.y;
            rotationZ[row] += weightedValue * rotation.z;
            positionX[row] += weightedValue * position.x;
            positionY[row] += weightedValue * position.y;
            positionZ[row] += weightedValue * position.z;
            for (int column = 0; column < features.Length; column++)
                normal[row, column] += weightedValue * features[column];
        }
    }

    private static void AddRidge(double[,] normal, float ridgeLambda)
    {
        for (int i = 0; i < normal.GetLength(0); i++)
            normal[i, i] += ridgeLambda;
    }

    private static double[][] SolveLinearSystems(double[,] sourceMatrix, params double[][] sourceVectors)
    {
        int size = sourceMatrix.GetLength(0);
        int outputCount = sourceVectors.Length;
        var matrix = new double[size, size + outputCount];
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
                matrix[row, column] = sourceMatrix[row, column];
            for (int output = 0; output < outputCount; output++)
                matrix[row, size + output] = sourceVectors[output][row];
        }

        for (int column = 0; column < size; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < size; row++)
            {
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                    pivot = row;
            }
            if (Math.Abs(matrix[pivot, column]) < 1e-12)
                continue;

            if (pivot != column)
            {
                for (int swapColumn = column; swapColumn < size + outputCount; swapColumn++)
                {
                    double value = matrix[column, swapColumn];
                    matrix[column, swapColumn] = matrix[pivot, swapColumn];
                    matrix[pivot, swapColumn] = value;
                }
            }

            double divisor = matrix[column, column];
            for (int normalizeColumn = column; normalizeColumn < size + outputCount; normalizeColumn++)
                matrix[column, normalizeColumn] /= divisor;
            for (int row = 0; row < size; row++)
            {
                if (row == column)
                    continue;

                double factor = matrix[row, column];
                if (Math.Abs(factor) < 1e-15)
                    continue;

                for (int eliminateColumn = column; eliminateColumn < size + outputCount; eliminateColumn++)
                    matrix[row, eliminateColumn] -= factor * matrix[column, eliminateColumn];
            }
        }

        var result = new double[outputCount][];
        for (int output = 0; output < outputCount; output++)
        {
            result[output] = new double[size];
            for (int row = 0; row < size; row++)
                result[output][row] = matrix[row, size + output];
        }
        return result;
    }

    private static void CopyCoefficients(double[] source, List<float> destination)
    {
        for (int i = 0; i < source.Length; i++)
            destination.Add((float)source[i]);
    }

    private static void CalculateFitErrors(
        IReadOnlyList<float[]> features,
        IReadOnlyList<Vector3> rotationTargets,
        IReadOnlyList<Vector3> positionTargets,
        double[] rotationX,
        double[] rotationY,
        double[] rotationZ,
        double[] positionX,
        double[] positionY,
        double[] positionZ,
        AvatarProfileCoupledMuscleCalibration calibration)
    {
        if (features.Count == 0)
            return;

        float rotationErrorSum = 0.0f;
        float angularErrorSum = 0.0f;
        float positionErrorSum = 0.0f;
        for (int i = 0; i < features.Count; i++)
        {
            Vector3 predictedRotation = EvaluateVector(features[i], rotationX, rotationY, rotationZ);
            Vector3 rotationDifference = predictedRotation - rotationTargets[i];
            float rotationError = rotationDifference.magnitude;
            Quaternion expected = FromRotationVector(rotationTargets[i]);
            Quaternion actual = FromRotationVector(predictedRotation);
            float angularError = Quaternion.Angle(expected, actual);
            Vector3 predictedPosition = EvaluateVector(features[i], positionX, positionY, positionZ);
            float positionError = (predictedPosition - positionTargets[i]).magnitude;
            rotationErrorSum += rotationError;
            angularErrorSum += angularError;
            positionErrorSum += positionError;
            calibration.MaxRotationVectorErrorRadians = Mathf.Max(calibration.MaxRotationVectorErrorRadians, rotationError);
            calibration.MaxAngularErrorDegrees = Mathf.Max(calibration.MaxAngularErrorDegrees, angularError);
            calibration.MaxPositionError = Mathf.Max(calibration.MaxPositionError, positionError);
        }

        float inverseCount = 1.0f / features.Count;
        calibration.MeanRotationVectorErrorRadians = rotationErrorSum * inverseCount;
        calibration.MeanAngularErrorDegrees = angularErrorSum * inverseCount;
        calibration.MeanPositionError = positionErrorSum * inverseCount;
    }

    private static Vector3 EvaluateVector(float[] features, double[] x, double[] y, double[] z)
    {
        var result = Vector3.zero;
        for (int i = 0; i < features.Length; i++)
        {
            result.x += features[i] * (float)x[i];
            result.y += features[i] * (float)y[i];
            result.z += features[i] * (float)z[i];
        }

        return result;
    }

    private static Vector3 ToShortestRotationVector(Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);
        if (rotation.w < 0.0f)
            rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);

        Vector3 axisTimesSinHalfAngle = new Vector3(rotation.x, rotation.y, rotation.z);
        float sinHalfAngle = axisTimesSinHalfAngle.magnitude;
        if (sinHalfAngle < 1e-7f)
            return axisTimesSinHalfAngle * 2.0f;

        float angle = 2.0f * Mathf.Atan2(sinHalfAngle, rotation.w);
        return axisTimesSinHalfAngle * (angle / sinHalfAngle);
    }

    private static Quaternion FromRotationVector(Vector3 value)
    {
        float angle = value.magnitude;
        if (angle < 1e-7f)
            return Quaternion.identity;

        return Quaternion.AngleAxis(angle * Mathf.Rad2Deg, value / angle);
    }

    private static AvatarHumanDescriptionSettings ReadAvatarSettings(Avatar avatar)
    {
        HumanDescription description = avatar.humanDescription;
        return new AvatarHumanDescriptionSettings
        {
            UpperArmTwist = description.upperArmTwist,
            LowerArmTwist = description.lowerArmTwist,
            UpperLegTwist = description.upperLegTwist,
            LowerLegTwist = description.lowerLegTwist,
            ArmStretch = description.armStretch,
            LegStretch = description.legStretch,
            FeetSpacing = description.feetSpacing,
            HasTranslationDoF = description.hasTranslationDoF,
        };
    }

    private static void PopulateMuscleDefaultRanges(PoseAuditReport report)
    {
        string[] muscleNames = HumanTrait.MuscleName;
        int muscleCount = Mathf.Min(muscleNames.Length, HumanTrait.MuscleCount);
        for (int i = 0; i < muscleCount; i++)
        {
            report.MuscleDefaultRanges.Add(new NamedFloatRange
            {
                Name = muscleNames[i],
                Min = HumanTrait.GetMuscleDefaultMin(i),
                Max = HumanTrait.GetMuscleDefaultMax(i),
            });
        }
    }

    private static List<RawCurveBinding> CollectRawCurveBindings(AnimationClip clip)
    {
        var bindings = new List<RawCurveBinding>();
#if UNITY_EDITOR
        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
                continue;

            bindings.Add(new RawCurveBinding
            {
                Path = binding.path ?? string.Empty,
                TypeName = binding.type != null ? binding.type.FullName ?? binding.type.Name : string.Empty,
                PropertyName = binding.propertyName ?? string.Empty,
                Curve = curve,
            });
        }

        bindings.Sort(static (a, b) =>
        {
            int path = string.CompareOrdinal(a.Path, b.Path);
            if (path != 0)
                return path;

            int typeName = string.CompareOrdinal(a.TypeName, b.TypeName);
            if (typeName != 0)
                return typeName;

            return string.CompareOrdinal(a.PropertyName, b.PropertyName);
        });
#endif
        return bindings;
    }

    private static ClipRootMotionSettings ReadRootMotionSettings(AnimationClip clip)
    {
        var result = new ClipRootMotionSettings
        {
            StartTime = 0.0f,
            StopTime = clip.length,
        };

#if UNITY_EDITOR
        var serializedClip = new SerializedObject(clip);
        serializedClip.UpdateIfRequiredOrScript();
        SerializedProperty settings = serializedClip.FindProperty("m_AnimationClipSettings");
        if (settings == null)
            return result;

        result.StartTime = ReadFloat(settings, "m_StartTime", result.StartTime);
        result.StopTime = ReadFloat(settings, "m_StopTime", result.StopTime);
        result.OrientationOffsetY = ReadFloat(settings, "m_OrientationOffsetY", 0.0f);
        result.Level = ReadFloat(settings, "m_Level", 0.0f);
        result.CycleOffset = ReadFloat(settings, "m_CycleOffset", 0.0f);
        result.LoopTime = ReadBool(settings, "m_LoopTime");
        result.LoopBlend = ReadBool(settings, "m_LoopBlend");
        result.BakeOrientationIntoPose = ReadBool(settings, "m_LoopBlendOrientation");
        result.BakePositionYIntoPose = ReadBool(settings, "m_LoopBlendPositionY");
        result.BakePositionXZIntoPose = ReadBool(settings, "m_LoopBlendPositionXZ");
        result.KeepOriginalOrientation = ReadBool(settings, "m_KeepOriginalOrientation");
        result.KeepOriginalPositionY = ReadBool(settings, "m_KeepOriginalPositionY");
        result.KeepOriginalPositionXZ = ReadBool(settings, "m_KeepOriginalPositionXZ");
        result.HeightFromFeet = ReadBool(settings, "m_HeightFromFeet");
        result.Mirror = ReadBool(settings, "m_Mirror");
#endif

        return result;
    }

#if UNITY_EDITOR
    private static float ReadFloat(SerializedProperty parent, string relativeName, float fallback)
    {
        SerializedProperty property = parent.FindPropertyRelative(relativeName);
        return property != null ? property.floatValue : fallback;
    }

    private static bool ReadBool(SerializedProperty parent, string relativeName)
    {
        SerializedProperty property = parent.FindPropertyRelative(relativeName);
        return property != null && property.boolValue;
    }

    private static string GetRequiredCommandLineArgument(string name)
    {
        string value = GetCommandLineArgument(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Missing required command-line argument '" + name + "'.");

        return value;
    }

    private static int GetOptionalIntCommandLineArgument(string name)
    {
        string value = GetCommandLineArgument(name);
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        if (int.TryParse(value, out int result) && result > 0)
            return result;

        throw new InvalidOperationException("Command-line argument '" + name + "' must be a positive integer.");
    }

    private static string GetCommandLineArgument(string name)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < arguments.Length ? arguments[i + 1] : string.Empty;

            string prefix = name + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return argument.Substring(prefix.Length);
        }

        return string.Empty;
    }
#endif

    private int ResolveSampleRate(AnimationClip clip)
    {
        if (SampleRateOverride > 0)
            return SampleRateOverride;

        float frameRate = clip.frameRate > 0.0f ? clip.frameRate : 30.0f;
        return Mathf.Max(1, Mathf.RoundToInt(frameRate));
    }

    private static string ResolveOutputPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "PoseAudit", "UnityHumanoidPose.json");

        if (Path.IsPathRooted(rawPath))
            return rawPath;

        return Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, rawPath));
    }

    private static void DisableBehaviours(GameObject cloneRoot)
    {
        Behaviour[] behaviours = cloneRoot.GetComponentsInChildren<Behaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            Behaviour behaviour = behaviours[i];
            if (behaviour is Animator)
                continue;

            behaviour.enabled = false;
        }
    }
}
