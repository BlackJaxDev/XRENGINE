using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Transforms;
using XREngine.Animation.IK;
using XREngine.Core.Reflection.Attributes;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Info;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
    public sealed class HumanoidPoseAuditOverlayComponent : XRComponent, IRenderable
    {
        private static readonly (string Parent, string Child)[] BoneLinks =
        [
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
        ];

        private static readonly ELimbEndEffector[] DiagnosticGoals =
        [
            ELimbEndEffector.LeftFoot,
            ELimbEndEffector.RightFoot,
            ELimbEndEffector.LeftHand,
            ELimbEndEffector.RightHand,
        ];

        private readonly record struct MuscleDebugLabel(
            string Name,
            EHumanoidValue Value,
            string BoneName,
            float Amount);

        private readonly RenderInfo3D _renderInfo;
        private string? _loadedReferencePath;
        private HumanoidPoseAuditReport? _referenceReport;
        private bool _referenceLoadFailed;
        private float? _resolvedReferenceScale;

        public RenderInfo RenderInfo => _renderInfo;
        public RenderInfo[] RenderedObjects { get; }

        private AnimationClipComponent? _targetClipComponent;
        /// <summary>
        /// Optional animation clip source. If unset, a sibling AnimationClipComponent is used.
        /// </summary>
        public AnimationClipComponent? TargetClipComponent
        {
            get => _targetClipComponent;
            set => SetField(ref _targetClipComponent, value);
        }

        private HumanoidComponent? _targetHumanoid;
        /// <summary>
        /// Optional humanoid target. If unset, a sibling HumanoidComponent is used.
        /// </summary>
        public HumanoidComponent? TargetHumanoid
        {
            get => _targetHumanoid;
            set => SetField(ref _targetHumanoid, value);
        }

        private string? _referencePath = "K:\\Unity\\Jax Main Avatars\\PoseAudit\\UnityHumanoidPose.json";
        /// <summary>
        /// Unity-exported humanoid pose audit report to visualize against the live engine pose.
        /// </summary>
        [InspectorPath(InspectorPathKind.File, InspectorPathFormat.Both, DialogMode = InspectorPathDialogMode.Open, Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Title = "Choose Reference Audit Path")]
        public string? ReferencePath
        {
            get => _referencePath;
            set => SetField(ref _referencePath, value);
        }

        private bool _showReferencePoints = true;
        public bool ShowReferencePoints
        {
            get => _showReferencePoints;
            set => SetField(ref _showReferencePoints, value);
        }

        private bool _showReferenceSkeleton = true;
        public bool ShowReferenceSkeleton
        {
            get => _showReferenceSkeleton;
            set => SetField(ref _showReferenceSkeleton, value);
        }

        private bool _autoScaleReferenceToAvatar = true;
        /// <summary>
        /// If true, scales the Unity reference skeleton to the engine avatar's current root-space bone distances.
        /// This is useful when the imported avatar scale differs from Unity's sampled humanoid body size.
        /// </summary>
        public bool AutoScaleReferenceToAvatar
        {
            get => _autoScaleReferenceToAvatar;
            set => SetField(ref _autoScaleReferenceToAvatar, value);
        }

        private float _referenceScale = 1.0f;
        /// <summary>
        /// Additional manual multiplier applied to the Unity reference skeleton in root space.
        /// </summary>
        public float ReferenceScale
        {
            get => _referenceScale;
            set => SetField(ref _referenceScale, Math.Max(0.01f, value));
        }

        private bool _showActualPoints = true;
        public bool ShowActualPoints
        {
            get => _showActualPoints;
            set => SetField(ref _showActualPoints, value);
        }

        private bool _showErrorLines = true;
        public bool ShowErrorLines
        {
            get => _showErrorLines;
            set => SetField(ref _showErrorLines, value);
        }

        private bool _showBoneNamesWithNoMatch;
        public bool ShowBoneNamesWithNoMatch
        {
            get => _showBoneNamesWithNoMatch;
            set => SetField(ref _showBoneNamesWithNoMatch, value);
        }

        private float _maxErrorMetersForColor = 0.25f;
        public float MaxErrorMetersForColor
        {
            get => _maxErrorMetersForColor;
            set => SetField(ref _maxErrorMetersForColor, Math.Max(0.001f, value));
        }

        private bool _showBoneRotationBasis = true;
        /// <summary>
        /// Draws the live engine bone rotation basis at each mapped humanoid bone.
        /// Colors: red=up, green=right, blue=forward.
        /// </summary>
        public bool ShowBoneRotationBasis
        {
            get => _showBoneRotationBasis;
            set => SetField(ref _showBoneRotationBasis, value);
        }

        private bool _showBodyTrajectory = true;
        /// <summary>Draws the reference Body-position path in the avatar root frame.</summary>
        public bool ShowBodyTrajectory
        {
            get => _showBodyTrajectory;
            set => SetField(ref _showBodyTrajectory, value);
        }

        private bool _showProjectedRoot = true;
        /// <summary>Draws reference and live projected-root position and yaw independently of Hips.</summary>
        public bool ShowProjectedRoot
        {
            get => _showProjectedRoot;
            set => SetField(ref _showProjectedRoot, value);
        }

        private bool _showHipsLocalTransform = true;
        /// <summary>Draws the reference composed Hips local transform against the live Hips.</summary>
        public bool ShowHipsLocalTransform
        {
            get => _showHipsLocalTransform;
            set => SetField(ref _showHipsLocalTransform, value);
        }

        private bool _showAuthoredIKGoals = true;
        /// <summary>Draws authored body-frame IK targets and their final world-space targets.</summary>
        public bool ShowAuthoredIKGoals
        {
            get => _showAuthoredIKGoals;
            set => SetField(ref _showAuthoredIKGoals, value);
        }

        private bool _showCompensationSource = true;
        /// <summary>Labels IK policy/contact status and draws the post-pose compensation vector.</summary>
        public bool ShowCompensationSource
        {
            get => _showCompensationSource;
            set => SetField(ref _showCompensationSource, value);
        }

        private float _boneBasisAxisLength = 0.12f;
        /// <summary>
        /// World-space length of the red/green/blue bone basis axes.
        /// Increase this to make the per-bone basis lines easier to see.
        /// </summary>
        public float BoneBasisAxisLength
        {
            get => _boneBasisAxisLength;
            set => SetField(ref _boneBasisAxisLength, Math.Max(0.001f, value));
        }

        private bool _showMuscleDebugText = true;
        public bool ShowMuscleDebugText
        {
            get => _showMuscleDebugText;
            set => SetField(ref _showMuscleDebugText, value);
        }

        private float _muscleDebugThreshold = 0.01f;
        public float MuscleDebugThreshold
        {
            get => _muscleDebugThreshold;
            set => SetField(ref _muscleDebugThreshold, Math.Max(0.0f, value));
        }

        private Vector3 _muscleDebugTextOffset = new(0.08f, 0.06f, 0.0f);
        public Vector3 MuscleDebugTextOffset
        {
            get => _muscleDebugTextOffset;
            set => SetField(ref _muscleDebugTextOffset, value);
        }

        private float _muscleDebugTextScale = 0.0012f;
        public float MuscleDebugTextScale
        {
            get => _muscleDebugTextScale;
            set => SetField(ref _muscleDebugTextScale, Math.Max(0.0001f, value));
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            if (propName == nameof(ReferencePath))
            {
                InvalidateReferenceCache();
            }
        }

        public HumanoidPoseAuditOverlayComponent()
        {
            RenderedObjects = [_renderInfo = RenderInfo3D.New(this, EDefaultRenderPass.OnTopForward, Render)];
            _renderInfo.Layer = DefaultLayers.GizmosIndex;
        }

        private void Render()
        {
            var humanoid = TargetHumanoid ?? GetSiblingComponent<HumanoidComponent>();
            if (humanoid is null)
                return;

            var clipComponent = TargetClipComponent ?? GetSiblingComponent<AnimationClipComponent>();
            if (clipComponent is null)
                return;

            HumanoidPoseAuditReport? report = EnsureReferenceReportLoaded();
            if (report?.Samples.Count is not > 0)
                return;

            if (humanoid.Hips.Node is null)
                humanoid.SetFromNode();
            if (humanoid.Hips.Node is null)
                return;

            float timeSeconds = clipComponent.PlaybackTime;
            if (!TryFindClosestSample(report.Samples, timeSeconds, out HumanoidPoseAuditSample? sample) || sample is null)
                return;

            Matrix4x4 rootWorld = humanoid.SceneNode.Transform.WorldMatrix;
            Matrix4x4 rootInverse = humanoid.SceneNode.Transform.InverseWorldMatrix;
            var referenceByName = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            var actualRootSpaceByName = new Dictionary<string, Vector3>(StringComparer.Ordinal);

            foreach (HumanoidPoseAuditBoneSample bone in sample.Bones)
            {
                SceneNode? actualNode = ResolveBoneNode(humanoid, bone.Name);
                Transform? actualTransform = actualNode?.GetTransformAs<Transform>(true);
                if (actualTransform is null)
                    continue;

                actualRootSpaceByName[bone.Name] = Vector3.Transform(actualTransform.WorldTranslation, rootInverse);
            }

            float referenceScale = ReferenceScale;
            if (AutoScaleReferenceToAvatar)
                referenceScale *= ResolveReferenceScale(sample, actualRootSpaceByName);

            foreach (HumanoidPoseAuditBoneSample bone in sample.Bones)
            {
                Vector3 referenceWorld = GetReferenceBoneWorldPosition(bone, rootWorld, referenceScale);
                referenceByName[bone.Name] = referenceWorld;

                if (ShowReferencePoints)
                    RuntimeAnimationHostServices.Current.RenderPoint(referenceWorld, ColorF4.Cyan);

                SceneNode? actualNode = ResolveBoneNode(humanoid, bone.Name);
                Transform? actualTransform = actualNode?.GetTransformAs<Transform>(true);
                if (actualTransform is null)
                    continue;

                Vector3 actualWorld = actualTransform.WorldTranslation;
                if (ShowActualPoints)
                    RuntimeAnimationHostServices.Current.RenderPoint(actualWorld, ColorF4.White);

                if (ShowBoneRotationBasis)
                    DrawBoneBasis(actualTransform, BoneBasisAxisLength);

                if (ShowErrorLines)
                {
                    float error = Vector3.Distance(actualWorld, referenceWorld);
                    RuntimeAnimationHostServices.Current.RenderLine(actualWorld, referenceWorld, GetErrorColor(error, MaxErrorMetersForColor));
                }
            }

            RenderBodyRootAndIKDiagnostics(humanoid, report, sample, rootWorld, referenceScale);

            if (ShowReferenceSkeleton)
            {
                foreach ((string parentName, string childName) in BoneLinks)
                {
                    if (!referenceByName.TryGetValue(parentName, out Vector3 parent) || !referenceByName.TryGetValue(childName, out Vector3 child))
                        continue;

                    RuntimeAnimationHostServices.Current.RenderLine(parent, child, ColorF4.Blue);
                }
            }

            RenderMuscleDebugText(humanoid);
        }

        private void RenderBodyRootAndIKDiagnostics(
            HumanoidComponent humanoid,
            HumanoidPoseAuditReport report,
            HumanoidPoseAuditSample sample,
            Matrix4x4 rootWorld,
            float referenceScale)
        {
            if (ShowBodyTrajectory)
            {
                Vector3 previousWorld = default;
                bool hasPrevious = false;
                for (int i = 0; i < report.Samples.Count; i++)
                {
                    Vector3 rootSpace = GetReferenceBodyTrajectoryRootPosition(report, report.Samples[i], referenceScale);
                    Vector3 world = Vector3.Transform(rootSpace, rootWorld);
                    if (hasPrevious)
                        RuntimeAnimationHostServices.Current.RenderLine(previousWorld, world, ColorF4.Yellow);
                    previousWorld = world;
                    hasPrevious = true;
                }

                Vector3 bodyWorld = Vector3.Transform(
                    GetReferenceBodyTrajectoryRootPosition(report, sample, referenceScale),
                    rootWorld);
                RuntimeAnimationHostServices.Current.RenderPoint(bodyWorld, ColorF4.Yellow);
            }

            if (ShowProjectedRoot)
            {
                Vector3 referenceRootSpace = ConvertReferencePosition(
                    report,
                    sample.ProjectedRootPosition.Value,
                    referenceScale);
                Quaternion referenceRootRotation = ConvertReferenceRotation(
                    report,
                    sample.ProjectedRootRotation.Value);
                Vector3 referenceWorld = Vector3.Transform(referenceRootSpace, rootWorld);
                Quaternion referenceWorldRotation = Quaternion.Normalize(
                    humanoid.SceneNode.Transform.WorldRotation * referenceRootRotation);
                RuntimeAnimationHostServices.Current.RenderPoint(referenceWorld, ColorF4.Magenta);
                DrawRotationBasis(referenceWorld, referenceWorldRotation, BoneBasisAxisLength * 1.25f);

                HumanoidProjectedRootPose live = humanoid.CurrentProjectedRootPose;
                if (live.Channels != EHumanoidProjectedRootChannels.None)
                {
                    Vector3 liveWorld = Vector3.Transform(live.Position, rootWorld);
                    Quaternion liveWorldRotation = Quaternion.Normalize(
                        humanoid.SceneNode.Transform.WorldRotation * live.Rotation);
                    RuntimeAnimationHostServices.Current.RenderPoint(liveWorld, ColorF4.White);
                    RuntimeAnimationHostServices.Current.RenderLine(liveWorld, referenceWorld, ColorF4.Magenta);
                    DrawRotationBasis(liveWorld, liveWorldRotation, BoneBasisAxisLength);
                }
            }

            if (ShowHipsLocalTransform)
                RenderHipsLocalTransform(humanoid, report, sample, referenceScale);

            if (ShowAuthoredIKGoals || ShowCompensationSource)
                RenderIKGoalDiagnostics(humanoid);
        }

        private static Vector3 GetReferenceBodyTrajectoryRootPosition(
            HumanoidPoseAuditReport report,
            HumanoidPoseAuditSample sample,
            float referenceScale)
        {
            if (!IsSourceReport(report))
                return sample.ConvertedBodyTranslationDelta.Value;

            float humanScale = float.IsFinite(report.AvatarHumanScale) && report.AvatarHumanScale > 0.0f
                ? report.AvatarHumanScale
                : 1.0f;
            return ConvertSourcePosition(sample.BodyPosition.Value) * (humanScale * referenceScale);
        }

        private void RenderHipsLocalTransform(
            HumanoidComponent humanoid,
            HumanoidPoseAuditReport report,
            HumanoidPoseAuditSample sample,
            float referenceScale)
        {
            Transform? hips = humanoid.Hips.Node?.GetTransformAs<Transform>(true);
            if (hips is null)
                return;

            HumanoidPoseAuditVector3? referencePosition = sample.ComposedHipsLocalPosition
                ?? sample.HipsLocalPosition
                ?? FindBone(sample, "Hips")?.LocalPosition;
            HumanoidPoseAuditQuaternion? referenceRotation = sample.ComposedHipsLocalRotation
                ?? sample.HipsLocalRotation
                ?? FindBone(sample, "Hips")?.LocalRotation;
            if (referencePosition is null || referenceRotation is null)
                return;

            TransformBase parent = hips.Parent ?? humanoid.SceneNode.Transform;
            Vector3 localPosition = ConvertReferencePosition(report, referencePosition.Value, referenceScale);
            Quaternion localRotation = ConvertReferenceRotation(report, referenceRotation.Value);
            Vector3 referenceWorld = parent.TransformPoint(localPosition, render: true);
            Quaternion referenceWorldRotation = Quaternion.Normalize(parent.WorldRotation * localRotation);
            RuntimeAnimationHostServices.Current.RenderPoint(referenceWorld, ColorF4.Orange);
            RuntimeAnimationHostServices.Current.RenderPoint(hips.WorldTranslation, ColorF4.White);
            RuntimeAnimationHostServices.Current.RenderLine(hips.WorldTranslation, referenceWorld, ColorF4.Orange);
            DrawRotationBasis(referenceWorld, referenceWorldRotation, BoneBasisAxisLength);
        }

        private void RenderIKGoalDiagnostics(HumanoidComponent humanoid)
        {
            HumanoidIKSolverComponent? solver = humanoid.SceneNode.GetComponent<HumanoidIKSolverComponent>();
            if (solver is null)
                return;

            for (int i = 0; i < DiagnosticGoals.Length; i++)
            {
                HumanoidIKGoalDiagnosticState diagnostic = solver.GetAnimatedIKGoalDiagnostic(DiagnosticGoals[i]);
                if (diagnostic.Status == EHumanoidIKGoalApplicationStatus.None)
                    continue;

                if (ShowAuthoredIKGoals)
                {
                    RuntimeAnimationHostServices.Current.RenderPoint(diagnostic.BodyFrameWorldPosition, ColorF4.Cyan);
                    RuntimeAnimationHostServices.Current.RenderPoint(diagnostic.FinalWorldPosition, ColorF4.Green);
                    DrawRotationBasis(
                        diagnostic.FinalWorldPosition,
                        diagnostic.FinalWorldRotation,
                        BoneBasisAxisLength * 0.75f);
                }

                if (!ShowCompensationSource)
                    continue;

                if (diagnostic.ContactCompensationOffset != Vector3.Zero)
                {
                    RuntimeAnimationHostServices.Current.RenderLine(
                        diagnostic.BodyFrameWorldPosition,
                        diagnostic.FinalWorldPosition,
                        ColorF4.Orange);
                }

                // This text is an opt-in diagnostic overlay; formatting allocation is outside
                // normal animation ticks and makes the policy/contact source explicit on screen.
                RuntimeAnimationHostServices.Current.RenderText(
                    diagnostic.FinalWorldPosition + Vector3.UnitY * MuscleDebugTextOffset.Y,
                    $"{diagnostic.Goal}: {diagnostic.Status}",
                    ColorF4.White,
                    MuscleDebugTextScale);
            }
        }

        private static HumanoidPoseAuditBoneSample? FindBone(HumanoidPoseAuditSample sample, string name)
        {
            for (int i = 0; i < sample.Bones.Count; i++)
                if (string.Equals(sample.Bones[i].Name, name, StringComparison.Ordinal))
                    return sample.Bones[i];
            return null;
        }

        private static Vector3 ConvertReferencePosition(
            HumanoidPoseAuditReport report,
            Vector3 value,
            float referenceScale)
            => IsSourceReport(report) ? ConvertSourcePosition(value) * referenceScale : value;

        private static Quaternion ConvertReferenceRotation(
            HumanoidPoseAuditReport report,
            Quaternion value)
            => IsSourceReport(report)
                ? Quaternion.Normalize(new Quaternion(value.X, -value.Y, -value.Z, value.W))
                : Quaternion.Normalize(value);

        private static Vector3 ConvertSourcePosition(Vector3 value)
            => new(-value.X, value.Y, value.Z);

        private static bool IsSourceReport(HumanoidPoseAuditReport report)
            => report.Source.StartsWith("Unity", StringComparison.OrdinalIgnoreCase);

        private HumanoidPoseAuditReport? EnsureReferenceReportLoaded()
        {
            if (string.IsNullOrWhiteSpace(ReferencePath))
                return null;

            if (_referenceReport is not null && string.Equals(_loadedReferencePath, ReferencePath, StringComparison.OrdinalIgnoreCase))
                return _referenceReport;

            if (_referenceLoadFailed && string.Equals(_loadedReferencePath, ReferencePath, StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                if (!File.Exists(ReferencePath))
                    throw new FileNotFoundException("Reference audit report was not found.", ReferencePath);

                _referenceReport = HumanoidPoseAuditIO.LoadReport(ReferencePath);
                _loadedReferencePath = ReferencePath;
                _referenceLoadFailed = false;
                _resolvedReferenceScale = null;
            }
            catch (Exception ex)
            {
                _referenceReport = null;
                _loadedReferencePath = ReferencePath;
                _referenceLoadFailed = true;
                _resolvedReferenceScale = null;
                Debug.LogWarning($"[HumanoidPoseAuditOverlay] Failed to load reference report '{ReferencePath}': {ex.Message}");
            }

            return _referenceReport;
        }

        private void InvalidateReferenceCache()
        {
            _loadedReferencePath = null;
            _referenceReport = null;
            _referenceLoadFailed = false;
            _resolvedReferenceScale = null;
        }

        public static bool TryFindClosestSample(IReadOnlyList<HumanoidPoseAuditSample> samples, float timeSeconds, out HumanoidPoseAuditSample? sample)
        {
            sample = null;
            if (samples.Count == 0)
                return false;

            int bestIndex = 0;
            float bestDelta = Math.Abs(samples[0].TimeSeconds - timeSeconds);
            for (int i = 1; i < samples.Count; i++)
            {
                float delta = Math.Abs(samples[i].TimeSeconds - timeSeconds);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIndex = i;
                }
            }

            sample = samples[bestIndex];
            return true;
        }

        public static Vector3 GetReferenceBoneWorldPosition(HumanoidPoseAuditBoneSample bone, Matrix4x4 rootWorld, float referenceScale = 1.0f)
            => Vector3.Transform(bone.RootSpacePosition.Value * referenceScale, rootWorld);

        public static float ComputeReferenceScale(HumanoidPoseAuditSample sample, IReadOnlyDictionary<string, Vector3> actualRootSpacePositions)
        {
            float ratioSum = 0.0f;
            int ratioCount = 0;
            var referenceByName = sample.Bones.ToDictionary(static x => x.Name, static x => x.RootSpacePosition.Value, StringComparer.Ordinal);

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

        private float ResolveReferenceScale(HumanoidPoseAuditSample sample, IReadOnlyDictionary<string, Vector3> actualRootSpacePositions)
        {
            if (_resolvedReferenceScale.HasValue)
                return _resolvedReferenceScale.Value;

            _resolvedReferenceScale = ComputeReferenceScale(sample, actualRootSpacePositions);
            return _resolvedReferenceScale.Value;
        }

        private static SceneNode? ResolveBoneNode(HumanoidComponent humanoid, string boneName)
            => boneName switch
            {
                "Hips" => humanoid.Hips.Node,
                "Spine" => humanoid.Spine.Node,
                "Chest" => humanoid.Chest.Node,
                "UpperChest" => humanoid.UpperChest.Node,
                "Neck" => humanoid.Neck.Node,
                "Head" => humanoid.Head.Node,
                "Jaw" => humanoid.Jaw.Node,
                "LeftEye" => humanoid.Left.Eye.Node,
                "RightEye" => humanoid.Right.Eye.Node,
                "LeftShoulder" => humanoid.Left.Shoulder.Node,
                "LeftUpperArm" => humanoid.Left.Arm.Node,
                "LeftLowerArm" => humanoid.Left.Elbow.Node,
                "LeftHand" => humanoid.Left.Wrist.Node,
                "RightShoulder" => humanoid.Right.Shoulder.Node,
                "RightUpperArm" => humanoid.Right.Arm.Node,
                "RightLowerArm" => humanoid.Right.Elbow.Node,
                "RightHand" => humanoid.Right.Wrist.Node,
                "LeftUpperLeg" => humanoid.Left.Leg.Node,
                "LeftLowerLeg" => humanoid.Left.Knee.Node,
                "LeftFoot" => humanoid.Left.Foot.Node,
                "LeftToes" => humanoid.Left.Toes.Node,
                "RightUpperLeg" => humanoid.Right.Leg.Node,
                "RightLowerLeg" => humanoid.Right.Knee.Node,
                "RightFoot" => humanoid.Right.Foot.Node,
                "RightToes" => humanoid.Right.Toes.Node,
                _ => null,
            };

        private static ColorF4 GetErrorColor(float errorMeters, float maxErrorMeters)
        {
            if (errorMeters <= maxErrorMeters * 0.1f)
                return ColorF4.Green;

            if (errorMeters <= maxErrorMeters * 0.35f)
                return ColorF4.Yellow;

            if (errorMeters <= maxErrorMeters * 0.7f)
                return ColorF4.Orange;

            return ColorF4.Red;
        }

        private static void DrawBoneBasis(Transform transform, float axisLength)
        {
            Vector3 origin = transform.WorldTranslation;
            RuntimeAnimationHostServices.Current.RenderLine(origin, origin + transform.WorldUp * axisLength, ColorF4.Red);
            RuntimeAnimationHostServices.Current.RenderLine(origin, origin + transform.WorldRight * axisLength, ColorF4.Green);
            RuntimeAnimationHostServices.Current.RenderLine(origin, origin + transform.WorldForward * axisLength, ColorF4.Blue);
            RuntimeAnimationHostServices.Current.RenderPoint(origin + transform.WorldUp * axisLength, ColorF4.Red);
            RuntimeAnimationHostServices.Current.RenderPoint(origin + transform.WorldRight * axisLength, ColorF4.Green);
            RuntimeAnimationHostServices.Current.RenderPoint(origin + transform.WorldForward * axisLength, ColorF4.Blue);
        }

        private static void DrawRotationBasis(Vector3 origin, Quaternion rotation, float axisLength)
        {
            Vector3 right = Vector3.Transform(Vector3.UnitX * axisLength, rotation);
            Vector3 up = Vector3.Transform(Vector3.UnitY * axisLength, rotation);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ * axisLength, rotation);
            RuntimeAnimationHostServices.Current.RenderLine(origin, origin + right, ColorF4.Red);
            RuntimeAnimationHostServices.Current.RenderLine(origin, origin + up, ColorF4.Green);
            RuntimeAnimationHostServices.Current.RenderLine(origin, origin + forward, ColorF4.Blue);
            RuntimeAnimationHostServices.Current.RenderPoint(origin + right, ColorF4.Red);
            RuntimeAnimationHostServices.Current.RenderPoint(origin + up, ColorF4.Green);
            RuntimeAnimationHostServices.Current.RenderPoint(origin + forward, ColorF4.Blue);
        }

        private void RenderMuscleDebugText(HumanoidComponent humanoid)
        {
            if (!ShowMuscleDebugText)
                return;

            Dictionary<string, List<MuscleDebugLabel>> labelsByBone = BuildMuscleDebugLabelsByBone(humanoid);
            foreach ((string boneName, List<MuscleDebugLabel> labels) in labelsByBone)
            {
                SceneNode? anchorNode = ResolveBoneNode(humanoid, boneName);
                TransformBase anchor = anchorNode?.Transform ?? humanoid.SceneNode.Transform;
                Vector3 worldPosition = anchor.TransformPoint(GetMuscleDebugLocalOffset(boneName), render: true);
                string text = BuildMuscleDebugText(labels.Select(static label => (label.Name, label.Amount)).ToArray());
                RuntimeAnimationHostServices.Current.RenderText(worldPosition, text, ColorF4.White, MuscleDebugTextScale);
            }
        }

        private Dictionary<string, List<MuscleDebugLabel>> BuildMuscleDebugLabelsByBone(HumanoidComponent humanoid)
        {
            var entriesByBone = new Dictionary<string, List<MuscleDebugLabel>>(StringComparer.Ordinal);
            foreach (ImportedHumanoidMuscleMap.MuscleEntry entry in ImportedHumanoidMuscleMap.OrderedMuscleEntries)
            {
                if (!humanoid.TryGetMuscleValue(entry.Value, out float value))
                    continue;

                if (Math.Abs(value) < MuscleDebugThreshold)
                    continue;

                string boneName = ResolveMuscleDebugBoneName(entry.Value);
                if (!entriesByBone.TryGetValue(boneName, out List<MuscleDebugLabel>? boneEntries))
                {
                    boneEntries = [];
                    entriesByBone.Add(boneName, boneEntries);
                }

                boneEntries.Add(new MuscleDebugLabel(
                    Name: entry.HumanTraitName,
                    Value: entry.Value,
                    BoneName: boneName,
                    Amount: value));
            }

            foreach (List<MuscleDebugLabel> boneEntries in entriesByBone.Values)
                boneEntries.Sort(static (a, b) => Math.Abs(b.Amount).CompareTo(Math.Abs(a.Amount)));

            return entriesByBone;
        }

        public static string BuildMuscleDebugText(IReadOnlyList<(string Name, float Amount)> labels)
            => string.Join("\n", labels.Select(static label => $"{label.Name}: {label.Amount:+0.000;-0.000;0.000}"));

        public static string ResolveMuscleDebugBoneName(EHumanoidValue value)
            => value switch
            {
                EHumanoidValue.SpineFrontBack or
                EHumanoidValue.SpineLeftRight or
                EHumanoidValue.SpineTwistLeftRight => "Spine",

                EHumanoidValue.ChestFrontBack or
                EHumanoidValue.ChestLeftRight or
                EHumanoidValue.ChestTwistLeftRight => "Chest",

                EHumanoidValue.UpperChestFrontBack or
                EHumanoidValue.UpperChestLeftRight or
                EHumanoidValue.UpperChestTwistLeftRight => "UpperChest",

                EHumanoidValue.NeckNodDownUp or
                EHumanoidValue.NeckTiltLeftRight or
                EHumanoidValue.NeckTurnLeftRight => "Neck",

                EHumanoidValue.HeadNodDownUp or
                EHumanoidValue.HeadTiltLeftRight or
                EHumanoidValue.HeadTurnLeftRight => "Head",

                EHumanoidValue.LeftEyeDownUp or EHumanoidValue.LeftEyeInOut => "LeftEye",
                EHumanoidValue.RightEyeDownUp or EHumanoidValue.RightEyeInOut => "RightEye",
                EHumanoidValue.JawClose or EHumanoidValue.JawLeftRight => "Jaw",

                EHumanoidValue.LeftShoulderDownUp or
                EHumanoidValue.LeftShoulderFrontBack => "LeftShoulder",

                EHumanoidValue.RightShoulderDownUp or
                EHumanoidValue.RightShoulderFrontBack => "RightShoulder",

                EHumanoidValue.LeftArmDownUp or
                EHumanoidValue.LeftArmFrontBack or
                EHumanoidValue.LeftArmTwistInOut => "LeftUpperArm",

                EHumanoidValue.RightArmDownUp or
                EHumanoidValue.RightArmFrontBack or
                EHumanoidValue.RightArmTwistInOut => "RightUpperArm",

                EHumanoidValue.LeftForearmStretch or
                EHumanoidValue.LeftForearmTwistInOut => "LeftLowerArm",

                EHumanoidValue.RightForearmStretch or
                EHumanoidValue.RightForearmTwistInOut => "RightLowerArm",

                EHumanoidValue.LeftHandDownUp or
                EHumanoidValue.LeftHandInOut or
                EHumanoidValue.LeftHandThumb1Stretched or
                EHumanoidValue.LeftHandThumbSpread or
                EHumanoidValue.LeftHandThumb2Stretched or
                EHumanoidValue.LeftHandThumb3Stretched or
                EHumanoidValue.LeftHandIndex1Stretched or
                EHumanoidValue.LeftHandIndexSpread or
                EHumanoidValue.LeftHandIndex2Stretched or
                EHumanoidValue.LeftHandIndex3Stretched or
                EHumanoidValue.LeftHandMiddle1Stretched or
                EHumanoidValue.LeftHandMiddleSpread or
                EHumanoidValue.LeftHandMiddle2Stretched or
                EHumanoidValue.LeftHandMiddle3Stretched or
                EHumanoidValue.LeftHandRing1Stretched or
                EHumanoidValue.LeftHandRingSpread or
                EHumanoidValue.LeftHandRing2Stretched or
                EHumanoidValue.LeftHandRing3Stretched or
                EHumanoidValue.LeftHandLittle1Stretched or
                EHumanoidValue.LeftHandLittleSpread or
                EHumanoidValue.LeftHandLittle2Stretched or
                EHumanoidValue.LeftHandLittle3Stretched => "LeftHand",

                EHumanoidValue.RightHandDownUp or
                EHumanoidValue.RightHandInOut or
                EHumanoidValue.RightHandThumb1Stretched or
                EHumanoidValue.RightHandThumbSpread or
                EHumanoidValue.RightHandThumb2Stretched or
                EHumanoidValue.RightHandThumb3Stretched or
                EHumanoidValue.RightHandIndex1Stretched or
                EHumanoidValue.RightHandIndexSpread or
                EHumanoidValue.RightHandIndex2Stretched or
                EHumanoidValue.RightHandIndex3Stretched or
                EHumanoidValue.RightHandMiddle1Stretched or
                EHumanoidValue.RightHandMiddleSpread or
                EHumanoidValue.RightHandMiddle2Stretched or
                EHumanoidValue.RightHandMiddle3Stretched or
                EHumanoidValue.RightHandRing1Stretched or
                EHumanoidValue.RightHandRingSpread or
                EHumanoidValue.RightHandRing2Stretched or
                EHumanoidValue.RightHandRing3Stretched or
                EHumanoidValue.RightHandLittle1Stretched or
                EHumanoidValue.RightHandLittleSpread or
                EHumanoidValue.RightHandLittle2Stretched or
                EHumanoidValue.RightHandLittle3Stretched => "RightHand",

                EHumanoidValue.LeftUpperLegFrontBack or
                EHumanoidValue.LeftUpperLegInOut or
                EHumanoidValue.LeftUpperLegTwistInOut => "LeftUpperLeg",

                EHumanoidValue.RightUpperLegFrontBack or
                EHumanoidValue.RightUpperLegInOut or
                EHumanoidValue.RightUpperLegTwistInOut => "RightUpperLeg",

                EHumanoidValue.LeftLowerLegStretch or
                EHumanoidValue.LeftLowerLegTwistInOut => "LeftLowerLeg",

                EHumanoidValue.RightLowerLegStretch or
                EHumanoidValue.RightLowerLegTwistInOut => "RightLowerLeg",

                EHumanoidValue.LeftFootUpDown or
                EHumanoidValue.LeftFootTwistInOut => "LeftFoot",

                EHumanoidValue.RightFootUpDown or
                EHumanoidValue.RightFootTwistInOut => "RightFoot",

                EHumanoidValue.LeftToesUpDown => "LeftToes",
                EHumanoidValue.RightToesUpDown => "RightToes",
                _ => "Hips",
            };

        private Vector3 GetMuscleDebugLocalOffset(string boneName)
        {
            float lateral = boneName.StartsWith("Left", StringComparison.Ordinal) ? -Math.Abs(MuscleDebugTextOffset.X) :
                boneName.StartsWith("Right", StringComparison.Ordinal) ? Math.Abs(MuscleDebugTextOffset.X) :
                0.0f;

            return new Vector3(
                lateral,
                MuscleDebugTextOffset.Y,
                MuscleDebugTextOffset.Z);
        }
    }
}
