using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

[ExecuteAlways]
public sealed class HumanoidPoseAuditExporter : MonoBehaviour
{
    [Serializable]
    private sealed class PoseAuditReport
    {
        public int SchemaVersion = 1;
        public string Source = "UnityMecanim";
        public string ClipName = string.Empty;
        public string AvatarName = string.Empty;
        public float DurationSeconds;
        public int SampleRate;
        public int SampleCount;
        public List<PoseAuditSample> Samples = new List<PoseAuditSample>();
    }

    [Serializable]
    private sealed class PoseAuditSample
    {
        public int Index;
        public float TimeSeconds;
        public PoseVector3 BodyPosition = new PoseVector3();
        public PoseQuaternion BodyRotation = new PoseQuaternion();
        public List<NamedFloat> Muscles = new List<NamedFloat>();
        public List<BoneSample> Bones = new List<BoneSample>();
    }

    [Serializable]
    private sealed class NamedFloat
    {
        public string Name = string.Empty;
        public float Value;
    }

    [Serializable]
    private sealed class BoneSample
    {
        public string Name = string.Empty;
        public PoseQuaternion LocalRotation = new PoseQuaternion();
        public PoseVector3 RootSpacePosition = new PoseVector3();
        public PoseVector3 WorldPosition = new PoseVector3();
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

    private static readonly BoneDefinition[] BonesToSample =
    {
        new BoneDefinition("Hips", HumanBodyBones.Hips),
        new BoneDefinition("Spine", HumanBodyBones.Spine),
        new BoneDefinition("Chest", HumanBodyBones.Chest),
        new BoneDefinition("UpperChest", HumanBodyBones.UpperChest),
        new BoneDefinition("Neck", HumanBodyBones.Neck),
        new BoneDefinition("Head", HumanBodyBones.Head),
        new BoneDefinition("Jaw", HumanBodyBones.Jaw),
        new BoneDefinition("LeftEye", HumanBodyBones.LeftEye),
        new BoneDefinition("RightEye", HumanBodyBones.RightEye),
        new BoneDefinition("LeftShoulder", HumanBodyBones.LeftShoulder),
        new BoneDefinition("LeftUpperArm", HumanBodyBones.LeftUpperArm),
        new BoneDefinition("LeftLowerArm", HumanBodyBones.LeftLowerArm),
        new BoneDefinition("LeftHand", HumanBodyBones.LeftHand),
        new BoneDefinition("RightShoulder", HumanBodyBones.RightShoulder),
        new BoneDefinition("RightUpperArm", HumanBodyBones.RightUpperArm),
        new BoneDefinition("RightLowerArm", HumanBodyBones.RightLowerArm),
        new BoneDefinition("RightHand", HumanBodyBones.RightHand),
        new BoneDefinition("LeftUpperLeg", HumanBodyBones.LeftUpperLeg),
        new BoneDefinition("LeftLowerLeg", HumanBodyBones.LeftLowerLeg),
        new BoneDefinition("LeftFoot", HumanBodyBones.LeftFoot),
        new BoneDefinition("LeftToes", HumanBodyBones.LeftToes),
        new BoneDefinition("RightUpperLeg", HumanBodyBones.RightUpperLeg),
        new BoneDefinition("RightLowerLeg", HumanBodyBones.RightLowerLeg),
        new BoneDefinition("RightFoot", HumanBodyBones.RightFoot),
        new BoneDefinition("RightToes", HumanBodyBones.RightToes),
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
                graph.Evaluate(0.0f);
                poseHandler.GetHumanPose(ref humanPose);

                report.Samples.Add(CaptureSample(animator, humanPose, sampleTime, i));
            }
        }
        finally
        {
            graph.Destroy();
        }

        return report;
    }

    private static PoseAuditSample CaptureSample(Animator animator, HumanPose humanPose, float timeSeconds, int index)
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

        Transform root = animator.transform;
        foreach (BoneDefinition bone in BonesToSample)
        {
            Transform boneTransform = animator.GetBoneTransform(bone.Bone);
            if (boneTransform == null)
                continue;

            sample.Bones.Add(new BoneSample
            {
                Name = bone.Name,
                LocalRotation = PoseQuaternion.From(boneTransform.localRotation),
                RootSpacePosition = PoseVector3.From(root.InverseTransformPoint(boneTransform.position)),
                WorldPosition = PoseVector3.From(boneTransform.position),
            });
        }

        return sample;
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
