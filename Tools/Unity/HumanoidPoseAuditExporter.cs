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
        public int SchemaVersion = 1;
        public string Source = "UnityMecanim";
        public string AvatarName = string.Empty;
        public float HumanScale;
        public AvatarHumanDescriptionSettings AvatarSettings = new();
        public List<AvatarProfileNeutralBone> NeutralPoseBoneRotations = new();
        public List<AvatarProfileMuscleResponse> MuscleResponses = new();
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
                AvatarProfileReport avatarProfile = CreateAvatarProfile(report);
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

    private static AvatarProfileReport CreateAvatarProfile(PoseAuditReport report)
    {
        var profile = new AvatarProfileReport
        {
            AvatarName = report.AvatarName,
            HumanScale = report.AvatarHumanScale,
            AvatarSettings = report.AvatarSettings,
        };

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

        return profile;
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
