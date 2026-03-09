using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class HumanoidPoseAuditOverlay : MonoBehaviour
{
    [Serializable]
    private sealed class PoseAuditReport
    {
        public int SchemaVersion = 2;
        public string Source = string.Empty;
        public string ClipName = string.Empty;
        public string AvatarName = string.Empty;
        public float DurationSeconds;
        public int SampleRate;
        public int SampleCount;
        public List<NamedFloatRange> MuscleDefaultRanges = new();
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
        public PoseVector3 RootSpacePosition = new();
        public PoseVector3 WorldPosition = new();
    }

    [Serializable]
    private sealed class PoseVector3
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3 ToUnity()
            => new(X, Y, Z);
    }

    [Serializable]
    private sealed class PoseQuaternion
    {
        public float X;
        public float Y;
        public float Z;
        public float W = 1.0f;

        public Quaternion ToUnity()
            => Quaternion.Normalize(new Quaternion(X, Y, Z, W));
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

    private readonly struct MuscleDebugLabel
    {
        public readonly string Name;
        public readonly float Amount;

        public MuscleDebugLabel(string name, float amount)
        {
            Name = name;
            Amount = amount;
        }
    }

    private readonly struct RuntimeLabel
    {
        public readonly Vector3 WorldPosition;
        public readonly string Text;

        public RuntimeLabel(Vector3 worldPosition, string text)
        {
            WorldPosition = worldPosition;
            Text = text;
        }
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

    private static readonly (string Parent, string Child)[] BoneLinks =
    {
        ("Hips", "Spine"),
        ("Spine", "Chest"),
        ("Chest", "UpperChest"),
        ("Chest", "Neck"),
        ("UpperChest", "Neck"),
        ("Neck", "Head"),
        ("Head", "Jaw"),
        ("Head", "LeftEye"),
        ("Head", "RightEye"),
        ("Chest", "LeftShoulder"),
        ("UpperChest", "LeftShoulder"),
        ("LeftShoulder", "LeftUpperArm"),
        ("LeftUpperArm", "LeftLowerArm"),
        ("LeftLowerArm", "LeftHand"),
        ("Chest", "RightShoulder"),
        ("UpperChest", "RightShoulder"),
        ("RightShoulder", "RightUpperArm"),
        ("RightUpperArm", "RightLowerArm"),
        ("RightLowerArm", "RightHand"),
        ("Hips", "LeftUpperLeg"),
        ("LeftUpperLeg", "LeftLowerLeg"),
        ("LeftLowerLeg", "LeftFoot"),
        ("LeftFoot", "LeftToes"),
        ("Hips", "RightUpperLeg"),
        ("RightUpperLeg", "RightLowerLeg"),
        ("RightLowerLeg", "RightFoot"),
        ("RightFoot", "RightToes"),
    };

    public Animator Animator;
    public AnimationClip Clip;
    public TextAsset ReferenceJson;
    public string ReferencePath = "PoseAudit/UnityHumanoidPose.json";
    public bool UseAnimatorPlaybackTime = true;
    public int AnimatorLayer;
    public float ManualTimeSeconds;

    public bool ShowReferencePoints = true;
    public bool ShowReferenceSkeleton = true;
    public bool AutoScaleReferenceToAvatar = true;
    public float ReferenceScale = 1.0f;
    public bool ShowActualPoints = true;
    public bool ShowErrorLines = true;
    public bool ShowBoneNamesWithNoMatch;
    public float MaxErrorMetersForColor = 0.25f;
    public bool ShowBoneRotationBasis = true;
    public float BoneBasisAxisLength = 0.12f;
    public bool ShowMuscleDebugText = true;
    public float MuscleDebugThreshold = 0.01f;
    public Vector3 MuscleDebugTextOffset = new(0.08f, 0.06f, 0.0f);
    public float PointRadius = 0.015f;
    public Camera RuntimeLabelCamera;

    private string _loadedReferenceKey;
    private PoseAuditReport _referenceReport;
    private bool _referenceLoadFailed;
    private Animator _cachedPoseAnimator;
    private Avatar _cachedAvatar;
    private HumanPoseHandler _humanPoseHandler;
    private GUIStyle _runtimeLabelStyle;
    private readonly List<RuntimeLabel> _runtimeLabels = new();

    private void OnValidate()
    {
        ReferenceScale = Mathf.Max(0.01f, ReferenceScale);
        MaxErrorMetersForColor = Mathf.Max(0.001f, MaxErrorMetersForColor);
        BoneBasisAxisLength = Mathf.Max(0.001f, BoneBasisAxisLength);
        MuscleDebugThreshold = Mathf.Max(0.0f, MuscleDebugThreshold);
        PointRadius = Mathf.Max(0.001f, PointRadius);
        InvalidateReferenceCache();
    }

    private void OnDisable()
    {
        _runtimeLabels.Clear();
        _humanPoseHandler = null;
        _cachedPoseAnimator = null;
        _cachedAvatar = null;
    }

    [ContextMenu("Reload Reference Report")]
    private void ReloadReferenceReport()
        => InvalidateReferenceCache();

    private void OnDrawGizmos()
    {
        RenderOverlay();
    }

    private void OnGUI()
    {
        if (!Application.isPlaying || !ShowMuscleDebugText || _runtimeLabels.Count == 0)
            return;

        Camera camera = RuntimeLabelCamera != null ? RuntimeLabelCamera : Camera.main;
        if (camera == null)
            return;

        GUIStyle style = GetRuntimeLabelStyle();
        foreach (RuntimeLabel label in _runtimeLabels)
        {
            Vector3 screenPoint = camera.WorldToScreenPoint(label.WorldPosition);
            if (screenPoint.z <= 0.0f)
                continue;

            const float labelWidth = 280.0f;
            float labelHeight = style.CalcHeight(new GUIContent(label.Text), labelWidth);
            Rect rect = new(
                screenPoint.x + 8.0f,
                Screen.height - screenPoint.y - labelHeight * 0.5f,
                labelWidth,
                Mathf.Max(36.0f, labelHeight + 4.0f));
            GUI.Label(rect, label.Text, style);
        }
    }

    private void RenderOverlay()
    {
        _runtimeLabels.Clear();

        Animator animator = Animator != null ? Animator : GetComponent<Animator>();
        if (animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isHuman)
            return;

        PoseAuditReport report = EnsureReferenceReportLoaded();
        if (report == null || report.Samples == null || report.Samples.Count == 0)
            return;

        float sampleTime = ResolveSampleTime(animator, report);
        if (!TryFindClosestSample(report.Samples, sampleTime, out PoseAuditSample sample) || sample == null)
            return;

        Transform root = animator.transform;
        var referenceByName = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var actualRootSpaceByName = new Dictionary<string, Vector3>(StringComparer.Ordinal);

        foreach (BoneSample bone in sample.Bones)
        {
            Transform boneTransform = ResolveBoneTransform(animator, bone.Name);
            if (boneTransform == null)
                continue;

            actualRootSpaceByName[bone.Name] = root.InverseTransformPoint(boneTransform.position);
        }

        float referenceScale = ReferenceScale;
        if (AutoScaleReferenceToAvatar)
            referenceScale *= ComputeReferenceScale(sample, actualRootSpaceByName);

        foreach (BoneSample bone in sample.Bones)
        {
            Vector3 referenceWorld = GetReferenceBoneWorldPosition(bone, root, referenceScale);
            referenceByName[bone.Name] = referenceWorld;

            if (ShowReferencePoints)
                DrawPoint(referenceWorld, Color.cyan);

            Transform actualTransform = ResolveBoneTransform(animator, bone.Name);
            if (actualTransform == null)
            {
                if (ShowBoneNamesWithNoMatch)
                    DrawWorldLabel(referenceWorld, bone.Name);

                continue;
            }

            Vector3 actualWorld = actualTransform.position;
            if (ShowActualPoints)
                DrawPoint(actualWorld, Color.white);

            if (ShowBoneRotationBasis)
                DrawBoneBasis(actualTransform, BoneBasisAxisLength);

            if (ShowErrorLines)
                DrawLine(actualWorld, referenceWorld, GetErrorColor(Vector3.Distance(actualWorld, referenceWorld), MaxErrorMetersForColor));
        }

        if (ShowReferenceSkeleton)
        {
            foreach ((string parentName, string childName) in BoneLinks)
            {
                if (!referenceByName.TryGetValue(parentName, out Vector3 parent) || !referenceByName.TryGetValue(childName, out Vector3 child))
                    continue;

                DrawLine(parent, child, Color.blue);
            }
        }

        RenderMuscleDebugText(animator);
    }

    private PoseAuditReport EnsureReferenceReportLoaded()
    {
        string referenceKey = GetReferenceKey();
        if (string.IsNullOrWhiteSpace(referenceKey))
            return null;

        if (_referenceReport != null && string.Equals(_loadedReferenceKey, referenceKey, StringComparison.Ordinal))
            return _referenceReport;

        if (_referenceLoadFailed && string.Equals(_loadedReferenceKey, referenceKey, StringComparison.Ordinal))
            return null;

        try
        {
            string json = LoadReferenceJsonText();
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("Reference audit report is empty.");

            PoseAuditReport report = JsonUtility.FromJson<PoseAuditReport>(json);
            if (report == null)
                throw new InvalidOperationException("Reference audit report could not be parsed.");

            _referenceReport = report;
            _loadedReferenceKey = referenceKey;
            _referenceLoadFailed = false;
        }
        catch (Exception ex)
        {
            _referenceReport = null;
            _loadedReferenceKey = referenceKey;
            _referenceLoadFailed = true;
            Debug.LogWarning("[HumanoidPoseAuditOverlay] Failed to load reference report '" + referenceKey + "': " + ex.Message, this);
        }

        return _referenceReport;
    }

    private void InvalidateReferenceCache()
    {
        _loadedReferenceKey = null;
        _referenceReport = null;
        _referenceLoadFailed = false;
    }

    private string GetReferenceKey()
    {
        if (ReferenceJson != null)
            return "text:" + ReferenceJson.GetInstanceID();

        string fullPath = ResolveReferencePath(ReferencePath);
        return string.IsNullOrWhiteSpace(fullPath) ? null : fullPath;
    }

    private string LoadReferenceJsonText()
    {
        if (ReferenceJson != null)
            return ReferenceJson.text;

        string fullPath = ResolveReferencePath(ReferencePath);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            throw new FileNotFoundException("Reference audit report was not found.", fullPath);

        return File.ReadAllText(fullPath);
    }

    private static string ResolveReferencePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        if (Path.IsPathRooted(rawPath))
            return rawPath;

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
            return rawPath;

        return Path.GetFullPath(Path.Combine(projectRoot, rawPath));
    }

    private float ResolveSampleTime(Animator animator, PoseAuditReport report)
    {
        float duration = Clip != null && Clip.length > 0.0f
            ? Clip.length
            : Mathf.Max(0.0f, report.DurationSeconds);

        if (duration <= 0.0f)
            return 0.0f;

        if (UseAnimatorPlaybackTime && Application.isPlaying && animator.isActiveAndEnabled)
        {
            AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(Mathf.Max(0, AnimatorLayer));
            return Mathf.Repeat(stateInfo.normalizedTime, 1.0f) * duration;
        }

        return Mathf.Clamp(ManualTimeSeconds, 0.0f, duration);
    }

    private static bool TryFindClosestSample(IReadOnlyList<PoseAuditSample> samples, float timeSeconds, out PoseAuditSample sample)
    {
        sample = null;
        if (samples == null || samples.Count == 0)
            return false;

        int bestIndex = 0;
        float bestDelta = Mathf.Abs(samples[0].TimeSeconds - timeSeconds);
        for (int i = 1; i < samples.Count; i++)
        {
            float delta = Mathf.Abs(samples[i].TimeSeconds - timeSeconds);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIndex = i;
            }
        }

        sample = samples[bestIndex];
        return true;
    }

    private static Vector3 GetReferenceBoneWorldPosition(BoneSample bone, Transform root, float referenceScale = 1.0f)
        => root.TransformPoint(bone.RootSpacePosition.ToUnity() * referenceScale);

    private static float ComputeReferenceScale(PoseAuditSample sample, IReadOnlyDictionary<string, Vector3> actualRootSpacePositions)
    {
        if (sample == null || sample.Bones == null || actualRootSpacePositions == null)
            return 1.0f;

        float ratioSum = 0.0f;
        int ratioCount = 0;
        var referenceByName = sample.Bones.ToDictionary(static x => x.Name, static x => x.RootSpacePosition.ToUnity(), StringComparer.Ordinal);

        foreach ((string startName, string endName) in BoneLinks)
        {
            if (!referenceByName.TryGetValue(startName, out Vector3 referenceStart) ||
                !referenceByName.TryGetValue(endName, out Vector3 referenceEnd) ||
                !actualRootSpacePositions.TryGetValue(startName, out Vector3 actualStart) ||
                !actualRootSpacePositions.TryGetValue(endName, out Vector3 actualEnd))
            {
                continue;
            }

            float referenceDistance = Vector3.Distance(referenceStart, referenceEnd);
            float actualDistance = Vector3.Distance(actualStart, actualEnd);
            if (referenceDistance <= 1e-5f || actualDistance <= 1e-5f)
                continue;

            ratioSum += actualDistance / referenceDistance;
            ratioCount++;
        }

        return ratioCount > 0 ? ratioSum / ratioCount : 1.0f;
    }

    private static Transform ResolveBoneTransform(Animator animator, string boneName)
    {
        HumanBodyBones bone = boneName switch
        {
            "Hips" => HumanBodyBones.Hips,
            "Spine" => HumanBodyBones.Spine,
            "Chest" => HumanBodyBones.Chest,
            "UpperChest" => HumanBodyBones.UpperChest,
            "Neck" => HumanBodyBones.Neck,
            "Head" => HumanBodyBones.Head,
            "Jaw" => HumanBodyBones.Jaw,
            "LeftEye" => HumanBodyBones.LeftEye,
            "RightEye" => HumanBodyBones.RightEye,
            "LeftShoulder" => HumanBodyBones.LeftShoulder,
            "LeftUpperArm" => HumanBodyBones.LeftUpperArm,
            "LeftLowerArm" => HumanBodyBones.LeftLowerArm,
            "LeftHand" => HumanBodyBones.LeftHand,
            "RightShoulder" => HumanBodyBones.RightShoulder,
            "RightUpperArm" => HumanBodyBones.RightUpperArm,
            "RightLowerArm" => HumanBodyBones.RightLowerArm,
            "RightHand" => HumanBodyBones.RightHand,
            "LeftUpperLeg" => HumanBodyBones.LeftUpperLeg,
            "LeftLowerLeg" => HumanBodyBones.LeftLowerLeg,
            "LeftFoot" => HumanBodyBones.LeftFoot,
            "LeftToes" => HumanBodyBones.LeftToes,
            "RightUpperLeg" => HumanBodyBones.RightUpperLeg,
            "RightLowerLeg" => HumanBodyBones.RightLowerLeg,
            "RightFoot" => HumanBodyBones.RightFoot,
            "RightToes" => HumanBodyBones.RightToes,
            _ => HumanBodyBones.LastBone,
        };

        return bone == HumanBodyBones.LastBone ? null : animator.GetBoneTransform(bone);
    }

    private static Color GetErrorColor(float errorMeters, float maxErrorMeters)
    {
        if (errorMeters <= maxErrorMeters * 0.1f)
            return Color.green;

        if (errorMeters <= maxErrorMeters * 0.35f)
            return Color.yellow;

        if (errorMeters <= maxErrorMeters * 0.7f)
            return new Color(1.0f, 0.5f, 0.0f, 1.0f);

        return Color.red;
    }

    private void DrawPoint(Vector3 position, Color color)
    {
        Gizmos.color = color;
        Gizmos.DrawSphere(position, PointRadius);
    }

    private static void DrawLine(Vector3 start, Vector3 end, Color color)
    {
        Gizmos.color = color;
        Gizmos.DrawLine(start, end);
    }

    private static void DrawBoneBasis(Transform bone, float axisLength)
    {
        Vector3 origin = bone.position;
        DrawLine(origin, origin + bone.up * axisLength, Color.red);
        DrawLine(origin, origin + bone.right * axisLength, Color.green);
        DrawLine(origin, origin + bone.forward * axisLength, Color.blue);

        Gizmos.color = Color.red;
        Gizmos.DrawSphere(origin + bone.up * axisLength, axisLength * 0.08f);
        Gizmos.color = Color.green;
        Gizmos.DrawSphere(origin + bone.right * axisLength, axisLength * 0.08f);
        Gizmos.color = Color.blue;
        Gizmos.DrawSphere(origin + bone.forward * axisLength, axisLength * 0.08f);
    }

    private void RenderMuscleDebugText(Animator animator)
    {
        if (!ShowMuscleDebugText)
            return;

        Dictionary<string, List<MuscleDebugLabel>> labelsByBone = BuildMuscleDebugLabelsByBone(animator);
        foreach ((string boneName, List<MuscleDebugLabel> labels) in labelsByBone)
        {
            Transform anchor = ResolveBoneTransform(animator, boneName);
            Vector3 worldPosition = anchor != null
                ? anchor.TransformPoint(GetMuscleDebugLocalOffset(boneName))
                : animator.transform.TransformPoint(GetMuscleDebugLocalOffset(boneName));

            DrawWorldLabel(worldPosition, BuildMuscleDebugText(labels.Select(static label => (label.Name, label.Amount)).ToArray()));
        }
    }

    private Dictionary<string, List<MuscleDebugLabel>> BuildMuscleDebugLabelsByBone(Animator animator)
    {
        HumanPoseHandler poseHandler = GetOrCreateHumanPoseHandler(animator);
        if (poseHandler == null)
            return new Dictionary<string, List<MuscleDebugLabel>>(StringComparer.Ordinal);

        var result = new Dictionary<string, List<MuscleDebugLabel>>(StringComparer.Ordinal);
        HumanPose pose = default;
        poseHandler.GetHumanPose(ref pose);

        string[] muscleNames = HumanTrait.MuscleName;
        int muscleCount = Mathf.Min(muscleNames.Length, pose.muscles != null ? pose.muscles.Length : 0);
        for (int i = 0; i < muscleCount; i++)
        {
            float value = pose.muscles[i];
            if (Mathf.Abs(value) < MuscleDebugThreshold)
                continue;

            string boneName = ResolveMuscleDebugBoneName(muscleNames[i]);
            if (!result.TryGetValue(boneName, out List<MuscleDebugLabel> entries))
            {
                entries = new List<MuscleDebugLabel>();
                result.Add(boneName, entries);
            }

            entries.Add(new MuscleDebugLabel(muscleNames[i], value));
        }

        foreach (List<MuscleDebugLabel> entries in result.Values)
            entries.Sort(static (a, b) => Mathf.Abs(b.Amount).CompareTo(Mathf.Abs(a.Amount)));

        return result;
    }

    private HumanPoseHandler GetOrCreateHumanPoseHandler(Animator animator)
    {
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            return null;

        if (_humanPoseHandler != null && animator == _cachedPoseAnimator && animator.avatar == _cachedAvatar)
            return _humanPoseHandler;

        _cachedPoseAnimator = animator;
        _cachedAvatar = animator.avatar;
        _humanPoseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
        return _humanPoseHandler;
    }

    private static string BuildMuscleDebugText(IReadOnlyList<(string Name, float Amount)> labels)
        => string.Join("\n", labels.Select(static label => label.Name + ": " + label.Amount.ToString("+0.000;-0.000;0.000")));

    private static string ResolveMuscleDebugBoneName(string muscleName)
    {
        if (string.IsNullOrWhiteSpace(muscleName))
            return "Hips";

        return muscleName switch
        {
            string value when value.StartsWith("Spine ", StringComparison.Ordinal) => "Spine",
            string value when value.StartsWith("Chest ", StringComparison.Ordinal) => "Chest",
            string value when value.StartsWith("UpperChest ", StringComparison.Ordinal) => "UpperChest",
            string value when value.StartsWith("Neck ", StringComparison.Ordinal) => "Neck",
            string value when value.StartsWith("Head ", StringComparison.Ordinal) => "Head",
            "Left Eye Down-Up" or "Left Eye In-Out" => "LeftEye",
            "Right Eye Down-Up" or "Right Eye In-Out" => "RightEye",
            string value when value.StartsWith("Jaw ", StringComparison.Ordinal) => "Jaw",
            string value when value.StartsWith("Left Shoulder ", StringComparison.Ordinal) => "LeftShoulder",
            string value when value.StartsWith("Right Shoulder ", StringComparison.Ordinal) => "RightShoulder",
            string value when value.StartsWith("Left Arm ", StringComparison.Ordinal) => "LeftUpperArm",
            string value when value.StartsWith("Right Arm ", StringComparison.Ordinal) => "RightUpperArm",
            string value when value.StartsWith("Left Forearm ", StringComparison.Ordinal) => "LeftLowerArm",
            string value when value.StartsWith("Right Forearm ", StringComparison.Ordinal) => "RightLowerArm",
            string value when value.StartsWith("Left Hand ", StringComparison.Ordinal) => "LeftHand",
            string value when value.StartsWith("Right Hand ", StringComparison.Ordinal) => "RightHand",
            string value when value.StartsWith("Left Thumb ", StringComparison.Ordinal) => "LeftHand",
            string value when value.StartsWith("Left Index ", StringComparison.Ordinal) => "LeftHand",
            string value when value.StartsWith("Left Middle ", StringComparison.Ordinal) => "LeftHand",
            string value when value.StartsWith("Left Ring ", StringComparison.Ordinal) => "LeftHand",
            string value when value.StartsWith("Left Little ", StringComparison.Ordinal) => "LeftHand",
            string value when value.StartsWith("Right Thumb ", StringComparison.Ordinal) => "RightHand",
            string value when value.StartsWith("Right Index ", StringComparison.Ordinal) => "RightHand",
            string value when value.StartsWith("Right Middle ", StringComparison.Ordinal) => "RightHand",
            string value when value.StartsWith("Right Ring ", StringComparison.Ordinal) => "RightHand",
            string value when value.StartsWith("Right Little ", StringComparison.Ordinal) => "RightHand",
            string value when value.StartsWith("Left Upper Leg ", StringComparison.Ordinal) => "LeftUpperLeg",
            string value when value.StartsWith("Right Upper Leg ", StringComparison.Ordinal) => "RightUpperLeg",
            string value when value.StartsWith("Left Lower Leg ", StringComparison.Ordinal) => "LeftLowerLeg",
            string value when value.StartsWith("Right Lower Leg ", StringComparison.Ordinal) => "RightLowerLeg",
            string value when value.StartsWith("Left Foot ", StringComparison.Ordinal) => "LeftFoot",
            string value when value.StartsWith("Right Foot ", StringComparison.Ordinal) => "RightFoot",
            "Left Toes Up-Down" => "LeftToes",
            "Right Toes Up-Down" => "RightToes",
            _ => "Hips",
        };
    }

    private Vector3 GetMuscleDebugLocalOffset(string boneName)
    {
        float lateral = boneName.StartsWith("Left", StringComparison.Ordinal) ? -Mathf.Abs(MuscleDebugTextOffset.x) :
            boneName.StartsWith("Right", StringComparison.Ordinal) ? Mathf.Abs(MuscleDebugTextOffset.x) :
            0.0f;

        return new Vector3(lateral, MuscleDebugTextOffset.y, MuscleDebugTextOffset.z);
    }

    private void DrawWorldLabel(Vector3 worldPosition, string text)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            Handles.color = Color.white;
            Handles.Label(worldPosition, text, GetEditorLabelStyle());
            return;
        }
#endif

        _runtimeLabels.Add(new RuntimeLabel(worldPosition, text));
    }

    private GUIStyle GetRuntimeLabelStyle()
    {
        if (_runtimeLabelStyle != null)
            return _runtimeLabelStyle;

        _runtimeLabelStyle = new GUIStyle(GUI.skin.label)
        {
            normal = { textColor = Color.white },
            alignment = TextAnchor.MiddleLeft,
            wordWrap = true,
            richText = false,
        };
        return _runtimeLabelStyle;
    }

#if UNITY_EDITOR
    private static GUIStyle s_editorLabelStyle;

    private static GUIStyle GetEditorLabelStyle()
    {
        if (s_editorLabelStyle != null)
            return s_editorLabelStyle;

        s_editorLabelStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            normal = { textColor = Color.white },
            wordWrap = true,
            alignment = TextAnchor.MiddleLeft,
        };
        return s_editorLabelStyle;
    }
#endif
}