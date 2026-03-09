using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public sealed class HumanoidFullPoseExporter : EditorWindow
{
    private Animator animator;

    private const float ProbeAmount = 0.01f;
    private const float MinAngleEpsilonDeg = 0.01f;

    [MenuItem("Tools/Humanoid/Full Mecanim Export")]
    private static void Init()
    {
        GetWindow<HumanoidFullPoseExporter>("Humanoid Mecanim Export");
    }

    [Serializable]
    private sealed class BoneExport
    {
        public string boneName;

        public Quaternion boneNeutralRotation;
        public Quaternion boneParentNeutralRotation;
        public Vector3 boneAxis;

        public Quaternion unityToOpenGLRotation;
        public Vector3 unityToOpenGLAxis;

        public int[] muscleIndices;
        public float[] muscleMin;
        public float[] muscleMax;
    }

    [Serializable]
    private sealed class ExportData
    {
        public List<BoneExport> bones = new();
        public string[] muscleNames;
    }

    private sealed class TransformSnapshot
    {
        public readonly Transform transform;
        public readonly Vector3 localPosition;
        public readonly Quaternion localRotation;
        public readonly Vector3 localScale;

        public TransformSnapshot(Transform transform)
        {
            this.transform = transform;
            localPosition = transform.localPosition;
            localRotation = transform.localRotation;
            localScale = transform.localScale;
        }

        public void Restore()
        {
            transform.localPosition = localPosition;
            transform.localRotation = localRotation;
            transform.localScale = localScale;
        }
    }

    private void OnGUI()
    {
        animator = (Animator)EditorGUILayout.ObjectField(
            "Animator",
            animator,
            typeof(Animator),
            true);

        if (!animator)
            return;

        if (GUILayout.Button("Export Mecanim Data"))
            Export();
    }

    private void Export()
    {
        if (!animator)
        {
            Debug.LogError("No Animator assigned.");
            return;
        }

        if (!animator.avatar)
        {
            Debug.LogError("Animator has no Avatar.");
            return;
        }

        if (!animator.avatar.isHuman)
        {
            Debug.LogError("Avatar must be humanoid.");
            return;
        }

        Transform avatarRoot = animator.transform;
        Avatar avatar = animator.avatar;
        HumanPoseHandler handler = new HumanPoseHandler(avatar, avatarRoot);

        Transform[] bones = BuildBoneArray(animator);
        List<TransformSnapshot> snapshots = CaptureSnapshots(bones);

        HumanPose originalPose = default;
        HumanPose neutralPose = default;

        try
        {
            handler.GetHumanPose(ref originalPose);

            neutralPose = originalPose;
            neutralPose.muscles = new float[HumanTrait.MuscleCount];
            handler.SetHumanPose(ref neutralPose);

            Quaternion[] neutralLocalRotations = CaptureLocalRotations(bones);
            Quaternion[] parentNeutralLocalRotations = CaptureParentLocalRotations(bones);

            var boneMuscles = new Dictionary<HumanBodyBones, List<int>>();
            var boneAxisSums = new Dictionary<HumanBodyBones, Vector3>();

            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone)
                    continue;

                boneMuscles[bone] = new List<int>();
                boneAxisSums[bone] = Vector3.zero;
            }

            ProbeMuscles(
                handler,
                neutralPose,
                bones,
                neutralLocalRotations,
                boneMuscles,
                boneAxisSums);

            handler.SetHumanPose(ref neutralPose);

            ExportData export = new ExportData
            {
                muscleNames = HumanTrait.MuscleName
            };

            foreach (HumanBodyBones boneEnum in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (boneEnum == HumanBodyBones.LastBone)
                    continue;

                Transform bone = bones[(int)boneEnum];
                if (!bone)
                    continue;

                Vector3 axis = boneAxisSums[boneEnum].sqrMagnitude > 1e-10f
                    ? boneAxisSums[boneEnum].normalized
                    : Vector3.forward;

                int[] muscleIndices = boneMuscles[boneEnum].ToArray();
                float[] mins = new float[muscleIndices.Length];
                float[] maxs = new float[muscleIndices.Length];

                for (int i = 0; i < muscleIndices.Length; i++)
                {
                    int muscleIndex = muscleIndices[i];
                    mins[i] = HumanTrait.GetMuscleDefaultMin(muscleIndex);
                    maxs[i] = HumanTrait.GetMuscleDefaultMax(muscleIndex);
                }

                Quaternion neutralRotation = NormalizeSafe(neutralLocalRotations[(int)boneEnum]);
                Quaternion parentNeutralRotation = NormalizeSafe(parentNeutralLocalRotations[(int)boneEnum]);

                export.bones.Add(new BoneExport
                {
                    boneName = boneEnum.ToString(),
                    boneNeutralRotation = neutralRotation,
                    boneParentNeutralRotation = parentNeutralRotation,
                    boneAxis = axis,
                    unityToOpenGLRotation = ConvertRotationUnityToOpenGL(neutralRotation),
                    unityToOpenGLAxis = ConvertAxisUnityToOpenGL(axis),
                    muscleIndices = muscleIndices,
                    muscleMin = mins,
                    muscleMax = maxs,
                });
            }

            string path = EditorUtility.SaveFilePanel(
                "Export Mecanim Data",
                "",
                "mecanim_export.json",
                "json");

            if (string.IsNullOrWhiteSpace(path))
                return;

            string json = JsonUtility.ToJson(export, true);
            File.WriteAllText(path, json);

            Debug.Log($"Export complete: {path}");
        }
        finally
        {
            handler.SetHumanPose(ref originalPose);

            for (int i = 0; i < snapshots.Count; i++)
                snapshots[i].Restore();
        }
    }

    private static Transform[] BuildBoneArray(Animator animator)
    {
        Transform[] bones = new Transform[(int)HumanBodyBones.LastBone];

        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (bone == HumanBodyBones.LastBone)
                continue;

            bones[(int)bone] = animator.GetBoneTransform(bone);
        }

        return bones;
    }

    private static List<TransformSnapshot> CaptureSnapshots(Transform[] bones)
    {
        List<TransformSnapshot> snapshots = new List<TransformSnapshot>(bones.Length);

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i])
                snapshots.Add(new TransformSnapshot(bones[i]));
        }

        return snapshots;
    }

    private static Quaternion[] CaptureLocalRotations(Transform[] bones)
    {
        Quaternion[] rotations = new Quaternion[bones.Length];

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i])
                rotations[i] = NormalizeSafe(bones[i].localRotation);
            else
                rotations[i] = Quaternion.identity;
        }

        return rotations;
    }

    private static Quaternion[] CaptureParentLocalRotations(Transform[] bones)
    {
        Quaternion[] rotations = new Quaternion[bones.Length];

        for (int i = 0; i < bones.Length; i++)
        {
            Transform bone = bones[i];
            if (bone && bone.parent)
                rotations[i] = NormalizeSafe(bone.parent.localRotation);
            else
                rotations[i] = Quaternion.identity;
        }

        return rotations;
    }

    private static void ProbeMuscles(
        HumanPoseHandler handler,
        HumanPose neutralPose,
        Transform[] bones,
        Quaternion[] neutralLocalRotations,
        Dictionary<HumanBodyBones, List<int>> boneMuscles,
        Dictionary<HumanBodyBones, Vector3> boneAxisSums)
    {
        int muscleCount = HumanTrait.MuscleCount;

        for (int muscleIndex = 0; muscleIndex < muscleCount; muscleIndex++)
        {
            HumanPose probePose = neutralPose;
            probePose.muscles = new float[muscleCount];
            probePose.muscles[muscleIndex] = ProbeAmount;
            handler.SetHumanPose(ref probePose);

            for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
            {
                Transform bone = bones[boneIndex];
                if (!bone)
                    continue;

                Quaternion neutralRotation = neutralLocalRotations[boneIndex];
                Quaternion delta = NormalizeSafe(bone.localRotation * Quaternion.Inverse(neutralRotation));
                delta.ToAngleAxis(out float angleDeg, out Vector3 axis);

                angleDeg = NormalizeAngleDegrees(angleDeg);
                if (Mathf.Abs(angleDeg) <= MinAngleEpsilonDeg || axis.sqrMagnitude <= 1e-10f)
                    continue;

                HumanBodyBones boneEnum = (HumanBodyBones)boneIndex;
                boneMuscles[boneEnum].Add(muscleIndex);

                Vector3 normalizedAxis = axis.normalized * Mathf.Sign(angleDeg);
                Vector3 accumulated = boneAxisSums[boneEnum];

                if (accumulated.sqrMagnitude > 1e-10f && Vector3.Dot(accumulated.normalized, normalizedAxis) < 0.0f)
                    normalizedAxis = -normalizedAxis;

                boneAxisSums[boneEnum] = accumulated + normalizedAxis * Mathf.Abs(angleDeg);
            }
        }
    }

    private static float NormalizeAngleDegrees(float angleDeg)
    {
        if (angleDeg > 180.0f)
            angleDeg -= 360.0f;

        return angleDeg;
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (mag <= 1e-8f)
            return Quaternion.identity;

        return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
    }

    private static Quaternion ConvertRotationUnityToOpenGL(Quaternion rotation)
    {
        Matrix4x4 flipHandedness = Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f));
        Matrix4x4 unityRotation = Matrix4x4.Rotate(NormalizeSafe(rotation));
        Matrix4x4 glRotation = flipHandedness * unityRotation * flipHandedness;
        return NormalizeSafe(glRotation.rotation);
    }

    private static Vector3 ConvertAxisUnityToOpenGL(Vector3 axis)
    {
        Vector3 converted = new Vector3(axis.x, axis.y, -axis.z);
        return converted.sqrMagnitude > 1e-10f ? converted.normalized : Vector3.forward;
    }
}