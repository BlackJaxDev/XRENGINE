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
        public int SchemaVersion = 3;
        public string Source = "UnityMecanim";
        public string ClipName = string.Empty;
        public string AvatarName = string.Empty;
        public float DurationSeconds;
        public int SampleRate;
        public int SampleCount;
        public List<NamedFloatRange> MuscleDefaultRanges = new();
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
        public List<NamedFloat> Muscles = new();
        public List<RawCurveSample> RawCurves = new();
        public List<BoneSample> Bones = new();
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
        public PoseQuaternion LocalRotation = new();
        public PoseQuaternion BindRelativeRotation = new();
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

        GameObject clone = Instantiate(Animator.gameObject);
        clone.hideFlags = HideFlags.HideAndDontSave;
        clone.name = Animator.gameObject.name + "_PoseAuditClone";

        try
        {
            DisableBehaviours(clone);

            Animator cloneAnimator = clone.GetComponent<Animator>();
            if (cloneAnimator == null || !cloneAnimator.isHuman)
                throw new InvalidOperationException("Cloned Animator is missing or not humanoid.");

            var report = SampleAnimator(cloneAnimator, Clip, ResolveSampleRate(Clip));
            string fullPath = ResolveOutputPath(OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllText(fullPath, JsonUtility.ToJson(report, true));
            Debug.Log("[HumanoidPoseAuditExporter] Wrote pose audit to " + fullPath);
        }
        finally
        {
            DestroyImmediate(clone);
        }
    }

    private PoseAuditReport SampleAnimator(Animator animator, AnimationClip clip, int sampleRate)
    {
        var report = new PoseAuditReport
        {
            ClipName = clip.name,
            AvatarName = animator.gameObject.name,
            DurationSeconds = clip.length,
            SampleRate = sampleRate,
        };

        PopulateMuscleDefaultRanges(report);
        List<RawCurveBinding> rawCurveBindings = CollectRawCurveBindings(clip);
    Dictionary<HumanBodyBones, Quaternion> bindLocalRotations = CaptureBindLocalRotations(animator);

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
            report.DefaultMusclePose = CaptureDefaultMusclePose(animator, poseHandler, bindLocalRotations);

            for (int i = 0; i < sampleCount; i++)
            {
                float sampleTime = sampleCount == 1
                    ? 0.0f
                    : Mathf.Min(i / (float)sampleRate, clip.length);

                playable.SetTime(sampleTime);
                graph.Evaluate(0.0f);
                poseHandler.GetHumanPose(ref humanPose);

                report.Samples.Add(CaptureSample(animator, humanPose, sampleTime, i, rawCurveBindings, bindLocalRotations));
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
        Animator animator,
        HumanPoseHandler poseHandler,
        IReadOnlyDictionary<HumanBodyBones, Quaternion> bindLocalRotations)
    {
        var defaultPose = new HumanPose
        {
            bodyPosition = Vector3.zero,
            bodyRotation = Quaternion.identity,
            muscles = new float[HumanTrait.MuscleCount],
        };

        poseHandler.SetHumanPose(ref defaultPose);
        animator.Update(0.0f);
        poseHandler.GetHumanPose(ref defaultPose);
        return CaptureSample(animator, defaultPose, 0.0f, -1, Array.Empty<RawCurveBinding>(), bindLocalRotations);
    }

    private static PoseAuditSample CaptureSample(
        Animator animator,
        HumanPose humanPose,
        float timeSeconds,
        int index,
        IReadOnlyList<RawCurveBinding> rawCurveBindings,
        IReadOnlyDictionary<HumanBodyBones, Quaternion> bindLocalRotations)
    {
        var sample = new PoseAuditSample
        {
            Index = index,
            TimeSeconds = timeSeconds,
            BodyPosition = PoseVector3.From(humanPose.bodyPosition),
            BodyRotation = PoseQuaternion.From(humanPose.bodyRotation),
        };

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

            sample.Bones.Add(new BoneSample
            {
                Name = bone.Name,
                LocalRotation = PoseQuaternion.From(boneTransform.localRotation),
                BindRelativeRotation = PoseQuaternion.From(Quaternion.Inverse(bindLocalRotation) * boneTransform.localRotation),
                RootSpacePosition = PoseVector3.From(root.InverseTransformPoint(boneTransform.position)),
                WorldPosition = PoseVector3.From(boneTransform.position),
            });
        }

        return sample;
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
