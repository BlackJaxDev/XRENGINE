using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Scene;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
    [RequireComponents(typeof(HumanoidComponent))]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.HumanoidIKSolverComponentEditor")]
    public class HumanoidIKSolverComponent : HumanoidIKComponentBase
    {
        private const float FullGoalWeight = 1.0f;

        public HumanoidIKSolverComponent()
        {
            _spine.IKPositionWeight = 0.0f;
        }

        public IKSolverLimb _leftFoot = new(ELimbEndEffector.LeftFoot) { _bendModifier = ELimbBendModifier.Target };
        public IKSolverLimb _rightFoot = new(ELimbEndEffector.RightFoot) { _bendModifier = ELimbBendModifier.Target };
        public IKSolverLimb _leftHand = new(ELimbEndEffector.LeftHand) { _bendModifier = ELimbBendModifier.Arm };
        public IKSolverLimb _rightHand = new(ELimbEndEffector.RightHand) { _bendModifier = ELimbBendModifier.Arm };
        public IKSolverFABRIK _spine = new();
        //public IKSolverLookAt lookAt = new IKSolverLookAt();
        //public IKSolverAim aim = new IKSolverAim();
        public TransformConstrainer _hips = new();
        private Vector3 _animatedLeftFootLocalPosition;
        private Vector3 _animatedRightFootLocalPosition;
        private Vector3 _animatedLeftHandLocalPosition;
        private Vector3 _animatedRightHandLocalPosition;
        private Quaternion _animatedLeftFootLocalRotation = Quaternion.Identity;
        private Quaternion _animatedRightFootLocalRotation = Quaternion.Identity;
        private Quaternion _animatedLeftHandLocalRotation = Quaternion.Identity;
        private Quaternion _animatedRightHandLocalRotation = Quaternion.Identity;
        // Unity humanoid IK rotations are authored in a canonical avatar-goal basis.
        // The per-avatar offsets are derived solely from compiled neutral transforms;
        // never capture a mutable animation pose as calibration data.
        private Quaternion _animatedLeftFootGoalRotationOffset = Quaternion.Identity;
        private Quaternion _animatedRightFootGoalRotationOffset = Quaternion.Identity;
        private Quaternion _animatedLeftHandGoalRotationOffset = Quaternion.Identity;
        private Quaternion _animatedRightHandGoalRotationOffset = Quaternion.Identity;
        private bool _animatedLeftFootGoalRotationOffsetInitialized;
        private bool _animatedRightFootGoalRotationOffsetInitialized;
        private bool _animatedLeftHandGoalRotationOffsetInitialized;
        private bool _animatedRightHandGoalRotationOffsetInitialized;
        private bool _ikGoalWarningLogged;
        private float _avatarFeetSpacing;
        private Vector3 _avatarBodyRight = -Vector3.UnitX;
        private float _avatarModelUnitsPerMeter;
        private int _goalBasisSchemaVersion = -1;
        private int _goalBasisDefinitionRevision = -1;
        private string? _goalBasisDefinitionContentSha256;
        private readonly AnimatedGoalFrame[] _stagedAnimatedGoalFrames = new AnimatedGoalFrame[4];
        private int _stagedAnimatedGoalMask;
        private bool _isStagingAnimatedGoalFrame;
        private bool _hasPendingAnimatedGoalFrame;
        private bool _rejectNextAnimationDrivenSolve;
        private bool _usesNativeHumanoidTransactionBaseline;
        private readonly HumanoidIKGoalDiagnosticState[] _animatedGoalDiagnostics =
        [
            HumanoidIKGoalDiagnosticState.Empty(ELimbEndEffector.LeftFoot),
            HumanoidIKGoalDiagnosticState.Empty(ELimbEndEffector.RightFoot),
            HumanoidIKGoalDiagnosticState.Empty(ELimbEndEffector.LeftHand),
            HumanoidIKGoalDiagnosticState.Empty(ELimbEndEffector.RightHand),
        ];

        public bool UpdateLeftFootTarget { get; set; } = true;
        public bool UpdateRightFootTarget { get; set; } = true;
        public bool UpdateLeftHandTarget { get; set; } = true;
        public bool UpdateRightHandTarget { get; set; } = true;

        public override void Visualize()
        {
            for (int i = 0; i < Limbs.Length; i++)
            {
                var limb = Limbs[i];
                var target = limb.TargetIKTransform;
                if (target is null)
                    continue;

                RuntimeAnimationHostServices.Current.RenderPoint(target.WorldTranslation, ColorF4.Green);
                if (limb._bone3._transform is not null)
                    RuntimeAnimationHostServices.Current.RenderLine(limb._bone3._transform.WorldTranslation, target.WorldTranslation, ColorF4.Green);
            }
        }

        private IKSolverLimb[]? _limbs;
        /// <summary>
        /// Gets the array containing all the limbs.
        /// </summary>
        public IKSolverLimb[] Limbs
        {
            get
            {
                if (_limbs == null || (_limbs != null && _limbs.Length != 4))
                    _limbs = [_leftFoot, _rightFoot, _leftHand, _rightHand];
                return _limbs!;
            }
        }

        private IKSolver[]? _ikSolvers;
        /// <summary>
        /// Gets the array containing all %IK solvers.
        /// </summary>
        public IKSolver[] IKSolvers
        {
            get
            {
                if (_ikSolvers is null || (_ikSolvers != null && _ikSolvers.Length != 5))
                    _ikSolvers = [_leftFoot, _rightFoot, _leftHand, _rightHand, _spine/*, lookAt, aim */];
                return _ikSolvers!;
            }
        }

        public void InitializeChains(HumanoidComponent humanoid, bool forceConvertTransforms = true)
        {
            RefreshAvatarSolverSettings();
            var root = humanoid.SceneNode.GetTransformAs<Transform>(forceConvertTransforms);

            // Assigning limbs from references
            _leftHand.SetChain(
                humanoid.Left.Arm.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Left.Elbow.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Left.Wrist.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                root);

            _rightHand.SetChain(
                humanoid.Right.Arm.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Right.Elbow.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Right.Wrist.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                root);

            _leftFoot.SetChain(
                humanoid.Left.Leg.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Left.Knee.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Left.Foot.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                root);

            _rightFoot.SetChain(
                humanoid.Right.Leg.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Right.Knee.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Right.Foot.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                root);

            // Assigning spine bones from references
            _spine.SetChain(
                [humanoid.Spine.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Chest.Node?.GetTransformAs<Transform>(forceConvertTransforms),
                humanoid.Neck.Node?.GetTransformAs<Transform>(forceConvertTransforms)],
                root);

            //// Assigning lookAt bones from references
            //lookAt.SetChain(
            //    humanoid.Spine.Node?.GetTransformAs<Transform>(forceConvertTransforms),
            //    humanoid.Head.Node?.GetTransformAs<Transform>(forceConvertTransforms),
            //    humanoid.EyesTarget.Node?.GetTransformAs<Transform>(forceConvertTransforms),
            //    root);

            //// Assigning Aim bones from references
            //aim.SetChain(
            //    humanoid.Spine.Node?.GetTransformAs<Transform>(false), 
            //    root);

            _leftFoot._goal = ELimbEndEffector.LeftFoot;
            _rightFoot._goal = ELimbEndEffector.RightFoot;
            _leftHand._goal = ELimbEndEffector.LeftHand;
            _rightHand._goal = ELimbEndEffector.RightHand;

            _leftFoot.RelativeIKSpaceTransform = Transform;
            _rightFoot.RelativeIKSpaceTransform = Transform;
            _leftHand.RelativeIKSpaceTransform = Transform;
            _rightHand.RelativeIKSpaceTransform = Transform;
        }

        public float GetIKPositionWeight(ELimbEndEffector goal)
            => GetGoalIK(goal)?.IKPositionWeight ?? 0f;

        public float GetIKRotationWeight(ELimbEndEffector goal)
            => GetGoalIK(goal)?.IKRotationWeight ?? 0f;

        public HumanoidIKGoalDiagnosticState GetAnimatedIKGoalDiagnostic(ELimbEndEffector goal)
        {
            int index = GetAnimatedGoalDiagnosticIndex(goal);
            return index >= 0
                ? _animatedGoalDiagnostics[index]
                : HumanoidIKGoalDiagnosticState.Empty(goal);
        }

        public void SetIKPositionWeight(ELimbEndEffector goal, float weight)
        {
            if (_isStagingAnimatedGoalFrame)
            {
                ref AnimatedGoalFrame frame = ref GetStagedAnimatedGoalFrame(goal);
                frame.PositionWeight = weight;
                _stagedAnimatedGoalMask |= 1 << GetAnimatedGoalDiagnosticIndex(goal);
                return;
            }
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            ik.IKPositionWeight = weight;
        }

        public void SetIKRotationWeight(ELimbEndEffector goal, float weight)
        {
            if (_isStagingAnimatedGoalFrame)
            {
                ref AnimatedGoalFrame frame = ref GetStagedAnimatedGoalFrame(goal);
                frame.RotationWeight = weight;
                _stagedAnimatedGoalMask |= 1 << GetAnimatedGoalDiagnosticIndex(goal);
                return;
            }
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            ik.IKRotationWeight = weight;
        }

        public void SetIKPositionX(ELimbEndEffector goal, float x)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            ik.RawIKPosition = new Vector3(x, ik.RawIKPosition.Y, ik.RawIKPosition.Z);
        }
        public void SetIKPositionY(ELimbEndEffector goal, float y)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            ik.RawIKPosition = new Vector3(ik.RawIKPosition.X, y, ik.RawIKPosition.Z);
        }
        public void SetIKPositionZ(ELimbEndEffector goal, float z)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            ik.RawIKPosition = new Vector3(ik.RawIKPosition.X, ik.RawIKPosition.Y, z);
        }
        public void SetIKPosition(ELimbEndEffector goal, Vector3 IKPosition)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            ik.RawIKPosition = IKPosition;
        }

        public void SetIKRotation(ELimbEndEffector goal, Quaternion IKRotation)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            ik.RawIKRotation = IKRotation;
        }

        public Vector3 GetIKPosition(ELimbEndEffector goal)
            => GetGoalIK(goal)?.RawIKPosition ?? Vector3.Zero;

        public Quaternion GetIKRotation(ELimbEndEffector goal)
            => GetGoalIK(goal)?.RawIKRotation ?? Quaternion.Identity;

        //public void SetLookAtWeight(float weight, float bodyWeight, float headWeight, float eyesWeight, float clampWeight, float clampWeightHead, float clampWeightEyes)
        //    => solvers.lookAt.SetLookAtWeight(weight, bodyWeight, headWeight, eyesWeight, clampWeight, clampWeightHead, clampWeightEyes);

        //public void SetLookAtPosition(Vector3 lookAtPosition)
        //    => solvers.lookAt.SetIKPosition(lookAtPosition);

        public void SetSpinePosition(Vector3 spinePosition)
            => _spine.RawIKPosition = spinePosition;
        public void SetSpineWeight(float weight)
            => _spine.IKPositionWeight = weight;

        public void ConfigureForAnimationDrivenGoals()
        {
            SetToDefaults();

            SetIKPositionWeight(ELimbEndEffector.LeftHand, FullGoalWeight);
            SetIKRotationWeight(ELimbEndEffector.LeftHand, FullGoalWeight);
            SetIKPositionWeight(ELimbEndEffector.RightHand, FullGoalWeight);
            SetIKRotationWeight(ELimbEndEffector.RightHand, FullGoalWeight);
            SetIKPositionWeight(ELimbEndEffector.LeftFoot, FullGoalWeight);
            SetIKRotationWeight(ELimbEndEffector.LeftFoot, FullGoalWeight);
            SetIKPositionWeight(ELimbEndEffector.RightFoot, FullGoalWeight);
            SetIKRotationWeight(ELimbEndEffector.RightFoot, FullGoalWeight);
            SetSpineWeight(0.0f);
        }

        /// <summary>Starts an allocation-free authored IK frame transaction.</summary>
        public void BeginAnimationDrivenGoalFrame()
        {
            // The native humanoid solve publishes the complete authored baseline
            // after animation evaluation. Once this path is active, BaseIK must not
            // reset bones ahead of that transaction: a rejected frame must retain
            // the previously committed post-IK pose byte-for-byte.
            _usesNativeHumanoidTransactionBaseline = true;
            _isStagingAnimatedGoalFrame = false;
            _hasPendingAnimatedGoalFrame = false;
            _stagedAnimatedGoalMask = 0;
            for (int i = 0; i < _stagedAnimatedGoalFrames.Length; i++)
            {
                ELimbEndEffector goal = (ELimbEndEffector)i;
                IKSolverLimb? limb = GetGoalIK(goal);
                _stagedAnimatedGoalFrames[i] = new AnimatedGoalFrame(
                    GetAnimatedGoalLocalPosition(goal), GetAnimatedGoalLocalRotation(goal),
                    limb?.IKPositionWeight ?? 0.0f, limb?.IKRotationWeight ?? 0.0f);
            }
            _isStagingAnimatedGoalFrame = true;
        }

        /// <summary>Validates the staged frame without mutating targets or limbs.</summary>
        public void CompleteAnimationDrivenGoalFrame()
        {
            if (!_isStagingAnimatedGoalFrame)
                return;
            _isStagingAnimatedGoalFrame = false;
            _hasPendingAnimatedGoalFrame = _stagedAnimatedGoalMask != 0 && IsValidStagedAnimatedGoalFrame();
            _rejectNextAnimationDrivenSolve = _stagedAnimatedGoalMask != 0 && !_hasPendingAnimatedGoalFrame;
        }

        /// <summary>True when the current authored IK frame failed validation.</summary>
        internal bool IsAnimationDrivenGoalFrameRejected
            => _rejectNextAnimationDrivenSolve;

        /// <summary>True when an authored IK-only frame still requires the native neutral pose transaction.</summary>
        internal bool HasPendingAnimationDrivenGoalFrame
            => _hasPendingAnimatedGoalFrame;

        /// <summary>True while an authored IK frame still requires commit or rejection.</summary>
        internal bool HasUnresolvedAnimationDrivenGoalFrame
            => _isStagingAnimatedGoalFrame
            || _hasPendingAnimatedGoalFrame
            || _stagedAnimatedGoalMask != 0;

        /// <summary>Rejects a partial or invalid authored IK frame without publishing any staged state.</summary>
        internal void RejectAnimationDrivenGoalFrame()
        {
            _isStagingAnimatedGoalFrame = false;
            _hasPendingAnimatedGoalFrame = false;
            _rejectNextAnimationDrivenSolve = true;
            _stagedAnimatedGoalMask = 0;
        }

        /// <summary>Commits only a validated authored IK frame after the pose/root transaction accepts.</summary>
        public void ResolveAnimationDrivenGoalFrame(bool poseAccepted)
        {
            if (!poseAccepted)
            {
                RejectAnimationDrivenGoalFrame();
                return;
            }
            if (_stagedAnimatedGoalMask == 0)
            {
                _hasPendingAnimatedGoalFrame = false;
                _rejectNextAnimationDrivenSolve = false;
                return;
            }
            if (!_hasPendingAnimatedGoalFrame)
            {
                RejectAnimationDrivenGoalFrame();
                return;
            }

            for (int i = 0; i < _stagedAnimatedGoalFrames.Length; i++)
            {
                if ((_stagedAnimatedGoalMask & (1 << i)) == 0)
                    continue;
                ELimbEndEffector goal = (ELimbEndEffector)i;
                AnimatedGoalFrame frame = _stagedAnimatedGoalFrames[i];
                SetAnimatedGoalLocalPosition(goal, frame.Position);
                SetAnimatedGoalLocalRotation(goal, Quaternion.Normalize(frame.Rotation));
                IKSolverLimb? limb = GetGoalIK(goal);
                if (limb is not null)
                {
                    limb.IKPositionWeight = frame.PositionWeight;
                    limb.IKRotationWeight = frame.RotationWeight;
                }
                UpdateAnimatedIKGoal(goal);
            }
            _hasPendingAnimatedGoalFrame = false;
            _rejectNextAnimationDrivenSolve = false;
            _stagedAnimatedGoalMask = 0;
        }

        /// <summary>
        /// Configures the optional post-pose contact layer used by animation-driven IK goals.
        /// Authored body-relative goal data remains unchanged; only the final world-space target
        /// receives the configured contact offset.
        /// </summary>
        public void ConfigureAnimatedGoalContactCompensation(
            EHumanoidContactCompensationMode mode,
            float planeHeight,
            float clearance,
            float weight)
        {
            HumanoidSettings settings = Humanoid.Settings;
            settings.ContactCompensationMode = mode;
            settings.ContactPlaneHeight = planeHeight;
            settings.ContactClearance = clearance;
            settings.ContactCompensationWeight = weight;
            RefreshAnimatedGoalTransforms();
        }

        public void ClearAnimatedIKGoals()
        {
            _isStagingAnimatedGoalFrame = false;
            _hasPendingAnimatedGoalFrame = false;
            _rejectNextAnimationDrivenSolve = false;
            _usesNativeHumanoidTransactionBaseline = false;
            _stagedAnimatedGoalMask = 0;
            ClearAnimatedIKGoal(ELimbEndEffector.LeftFoot);
            ClearAnimatedIKGoal(ELimbEndEffector.RightFoot);
            ClearAnimatedIKGoal(ELimbEndEffector.LeftHand);
            ClearAnimatedIKGoal(ELimbEndEffector.RightHand);
        }

        public void SetAnimatedIKPosition(ELimbEndEffector goal, Vector3 position)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            if (_isStagingAnimatedGoalFrame)
            {
                ref AnimatedGoalFrame frame = ref GetStagedAnimatedGoalFrame(goal);
                frame.Position = position;
                _stagedAnimatedGoalMask |= 1 << GetAnimatedGoalDiagnosticIndex(goal);
                return;
            }
            SetAnimatedGoalLocalPosition(goal, position);
            UpdateAnimatedIKGoal(goal);
        }

        public void SetAnimatedIKPositionX(ELimbEndEffector goal, float x)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            Vector3 position = GetAnimatedGoalLocalPosition(goal);
            position.X = x;
            SetAnimatedGoalLocalPosition(goal, position);
        }

        public void SetAnimatedIKPositionY(ELimbEndEffector goal, float y)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            Vector3 position = GetAnimatedGoalLocalPosition(goal);
            position.Y = y;
            SetAnimatedGoalLocalPosition(goal, position);
        }

        public void SetAnimatedIKPositionZ(ELimbEndEffector goal, float z)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            Vector3 position = GetAnimatedGoalLocalPosition(goal);
            position.Z = z;
            SetAnimatedGoalLocalPosition(goal, position);
            UpdateAnimatedIKGoal(goal);
        }

        public void SetAnimatedIKRotation(ELimbEndEffector goal, Quaternion rotation)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            if (_isStagingAnimatedGoalFrame)
            {
                ref AnimatedGoalFrame frame = ref GetStagedAnimatedGoalFrame(goal);
                frame.Rotation = rotation;
                _stagedAnimatedGoalMask |= 1 << GetAnimatedGoalDiagnosticIndex(goal);
                return;
            }
            SetAnimatedGoalLocalRotation(goal, rotation);
            UpdateAnimatedIKGoal(goal);
        }

        public void SetAnimatedIKRotationX(ELimbEndEffector goal, float x)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            Quaternion rotation = GetAnimatedGoalLocalRotation(goal);
            rotation.X = x;
            SetAnimatedGoalLocalRotation(goal, rotation);
        }

        public void SetAnimatedIKRotationY(ELimbEndEffector goal, float y)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            Quaternion rotation = GetAnimatedGoalLocalRotation(goal);
            rotation.Y = y;
            SetAnimatedGoalLocalRotation(goal, rotation);
        }

        public void SetAnimatedIKRotationZ(ELimbEndEffector goal, float z)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            Quaternion rotation = GetAnimatedGoalLocalRotation(goal);
            rotation.Z = z;
            SetAnimatedGoalLocalRotation(goal, rotation);
        }

        public void SetAnimatedIKRotationW(ELimbEndEffector goal, float w)
        {
            if (!ShouldApplyAnimatedIKGoal(goal))
                return;

            Quaternion rotation = GetAnimatedGoalLocalRotation(goal);
            rotation.W = w;
            SetAnimatedGoalLocalRotation(goal, rotation);
            UpdateAnimatedIKGoal(goal);
        }

        public void SetAnimatedFootPosition(Vector3 position, bool leftFoot)
            => SetAnimatedIKPosition(leftFoot ? ELimbEndEffector.LeftFoot : ELimbEndEffector.RightFoot, position);

        public void SetAnimatedFootRotation(Quaternion rotation, bool leftFoot)
            => SetAnimatedIKRotation(leftFoot ? ELimbEndEffector.LeftFoot : ELimbEndEffector.RightFoot, rotation);

        public void SetAnimatedHandPosition(Vector3 position, bool leftHand)
            => SetAnimatedIKPosition(leftHand ? ELimbEndEffector.LeftHand : ELimbEndEffector.RightHand, position);

        public void SetAnimatedHandRotation(Quaternion rotation, bool leftHand)
            => SetAnimatedIKRotation(leftHand ? ELimbEndEffector.LeftHand : ELimbEndEffector.RightHand, rotation);

        public IKSolverLimb? GetGoalIK(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => _leftFoot,
            ELimbEndEffector.RightFoot => _rightFoot,
            ELimbEndEffector.LeftHand => _leftHand,
            ELimbEndEffector.RightHand => _rightHand,
            _ => null,
        };

        public void SetToDefaults()
        {
            foreach (IKSolverLimb limb in Limbs)
            {
                limb.IKPositionWeight = 0f;
                limb.IKRotationWeight = 0f;
                limb._bendModifier = ELimbBendModifier.Animation;
                limb._bendModifierWeight = 1f;
            }

            _leftHand._maintainRotationWeight = 0f;
            _rightHand._maintainRotationWeight = 0f;

            _spine.IKPositionWeight = 0f;
            _spine._tolerance = 0f;
            _spine._maxIterations = 2;
            _spine._useRotationLimits = false;

            //// Aim
            //solvers.aim.SetIKPositionWeight(0f);
            //solvers.aim.tolerance = 0f;
            //solvers.aim.maxIterations = 2;

            // LookAt
            //SetLookAtWeight(0f, 0.5f, 1f, 1f, 0.5f, 0.7f, 0.5f);
        }

        protected override bool ShouldApplySolverPose()
            => !_rejectNextAnimationDrivenSolve && base.ShouldApplySolverPose();

        protected override void ResetTransformsToDefault()
        {
            if (_usesNativeHumanoidTransactionBaseline)
                return;

            _hips.ResetTransformToDefault();
            //solvers.lookAt.ResetTransformToDefault();
            for (int i = 0; i < Limbs.Length; i++)
                Limbs[i].ResetTransformToDefault();
        }

        protected override void InitializeSolver()
        {
            InitializeChains(Humanoid);
            ResetAnimatedGoalRotationOffsets();

            var rootTfm = Root ?? SceneNode.GetTransformAs<Transform>(true)!;

            if (_spine._bones.Length > 1)
                _spine.Initialize(rootTfm);

            //solvers.lookAt.Initiate(Transform);
            //solvers.aim.Initiate(Transform);

            foreach (IKSolverLimb limb in Limbs)
                limb.Initialize(rootTfm);

            _hips.Transform = Humanoid.Hips.Node?.GetTransformAs<Transform>(true)!;
        }

        protected override void UpdateSolver()
        {
            if (_rejectNextAnimationDrivenSolve)
                return;
            RefreshAvatarSolverSettings();
            RefreshAnimatedGoalTransforms();

            for (int i = 0; i < Limbs.Length; i++)
            {
                Limbs[i].MaintainBend();
                Limbs[i].MaintainRotation();
            }

            _hips.Update();

            if (_spine._bones.Length > 1)
                _spine.Update();

            //solvers.aim.Update();
            //solvers.lookAt.Update();

            for (int i = 0; i < Limbs.Length; i++)
                Limbs[i].Update();
        }

        private bool ShouldApplyAnimatedIKGoal(ELimbEndEffector goal)
        {
            RefreshAvatarSolverSettings();
            switch (Humanoid.Settings.IKGoalPolicy)
            {
                case EHumanoidIKGoalPolicy.AlwaysApply:
                    return true;
                case EHumanoidIKGoalPolicy.ApplyIfCalibrated:
                    if (TryEnsureAnimatedGoalRotationOffset(goal))
                        return true;

                    if (!_ikGoalWarningLogged)
                    {
                        _ikGoalWarningLogged = true;
                        Debug.Animation("[HumanoidIKSolverComponent] IK goal channels present but the compiled avatar has no valid neutral goal basis; skipping animation-driven IK goals.");
                    }
                    RecordAnimatedGoalStatus(goal, EHumanoidIKGoalApplicationStatus.SkippedUncalibrated);
                    return false;
                default:
                    RecordAnimatedGoalStatus(goal, EHumanoidIKGoalApplicationStatus.IgnoredByPolicy);
                    return false;
            }
        }

        private void ClearAnimatedIKGoal(ELimbEndEffector goal)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            var target = GetAnimatedGoalTransform(goal);
            if (ReferenceEquals(ik.TargetIKTransform, target))
                ik.TargetIKTransform = null;

            ik.RawIKPosition = Vector3.Zero;
            ik.RawIKRotation = Quaternion.Identity;
            ik.IKPositionWeight = 0.0f;
            ik.IKRotationWeight = 0.0f;

            SetAnimatedGoalLocalPosition(goal, Vector3.Zero);
            SetAnimatedGoalLocalRotation(goal, Quaternion.Identity);
            ResetAnimatedGoalRotationOffset(goal);

            int diagnosticIndex = GetAnimatedGoalDiagnosticIndex(goal);
            if (diagnosticIndex >= 0)
                _animatedGoalDiagnostics[diagnosticIndex] = HumanoidIKGoalDiagnosticState.Empty(goal);
        }

        private void UpdateAnimatedIKGoal(ELimbEndEffector goal)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            var target = EnsureAnimatedGoalTransform(goal);
            ik.TargetIKTransform = target;

            if (!ShouldUpdateAnimatedGoalTarget(goal))
                return;

            RefreshAnimatedGoalTransform(goal);
        }

        private void RefreshAnimatedGoalTransforms()
        {
            RefreshAnimatedGoalTransform(ELimbEndEffector.LeftFoot);
            RefreshAnimatedGoalTransform(ELimbEndEffector.RightFoot);
            RefreshAnimatedGoalTransform(ELimbEndEffector.LeftHand);
            RefreshAnimatedGoalTransform(ELimbEndEffector.RightHand);
            EnforceAnimatedFeetSpacing();
        }

        private void RefreshAvatarSolverSettings()
        {
            bool hasCompiledAvatar = Humanoid.TryGetCompiledAvatarIKSettings(
                    out float armStretch,
                    out float legStretch,
                    out _avatarFeetSpacing,
                    out _avatarBodyRight,
                    out _avatarModelUnitsPerMeter,
                    out int schemaVersion,
                    out int definitionRevision,
                    out string definitionContentSha256);
            if (hasCompiledAvatar)
            {
                if (_goalBasisSchemaVersion != schemaVersion
                    || _goalBasisDefinitionRevision != definitionRevision
                    || !string.Equals(_goalBasisDefinitionContentSha256, definitionContentSha256, StringComparison.Ordinal))
                {
                    ResetAnimatedGoalRotationOffsets();
                    _goalBasisSchemaVersion = schemaVersion;
                    _goalBasisDefinitionRevision = definitionRevision;
                    _goalBasisDefinitionContentSha256 = definitionContentSha256;
                }

            }
            else
            {
                if (_goalBasisSchemaVersion != -1
                    || _goalBasisDefinitionRevision != -1
                    || _goalBasisDefinitionContentSha256 is not null)
                {
                    ResetAnimatedGoalRotationOffsets();
                    _goalBasisSchemaVersion = -1;
                    _goalBasisDefinitionRevision = -1;
                    _goalBasisDefinitionContentSha256 = null;
                }

                armStretch = 0.0f;
                legStretch = 0.0f;
                _avatarFeetSpacing = 0.0f;
                _avatarBodyRight = -Vector3.UnitX;
                _avatarModelUnitsPerMeter = 0.0f;
            }

            _leftHand.StretchAllowance = armStretch;
            _rightHand.StretchAllowance = armStretch;
            _leftFoot.StretchAllowance = legStretch;
            _rightFoot.StretchAllowance = legStretch;
        }

        private void EnforceAnimatedFeetSpacing()
        {
            if (_avatarFeetSpacing <= 0.0f
                || _leftFoot.IKPositionWeight <= 0.0f
                || _rightFoot.IKPositionWeight <= 0.0f)
                return;

            Transform? leftTarget = GetAnimatedGoalTransform(ELimbEndEffector.LeftFoot);
            Transform? rightTarget = GetAnimatedGoalTransform(ELimbEndEffector.RightFoot);
            if (leftTarget is null
                || rightTarget is null
                || !ReferenceEquals(_leftFoot.TargetIKTransform, leftTarget)
                || !ReferenceEquals(_rightFoot.TargetIKTransform, rightTarget))
                return;

            Transform? modelRoot = Root;
            if (modelRoot is null)
                return;

            modelRoot.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
            Vector3 worldRight = Vector3.TransformNormal(_avatarBodyRight, modelRoot.WorldMatrix);
            float worldRightLengthSquared = worldRight.LengthSquared();
            if (!float.IsFinite(worldRightLengthSquared) || worldRightLengthSquared <= 1e-8f)
                return;
            worldRight /= MathF.Sqrt(worldRightLengthSquared);

            // FeetSpacing is an authored world-space distance. Convert its meter
            // representation into the compiled model's units; human height only
            // scales animation deltas and must not rescale this absolute constraint.
            float minimumSpacing = _avatarFeetSpacing * _avatarModelUnitsPerMeter;
            if (!float.IsFinite(minimumSpacing) || minimumSpacing <= 0.0f)
                return;

            Vector3 leftPosition = leftTarget.WorldTranslation;
            Vector3 rightPosition = rightTarget.WorldTranslation;
            float lateralSpacing = Vector3.Dot(rightPosition - leftPosition, worldRight);
            if (!float.IsFinite(lateralSpacing) || lateralSpacing >= minimumSpacing)
                return;

            float halfCorrection = (minimumSpacing - lateralSpacing) * 0.5f;
            Vector3 leftOffset = -worldRight * halfCorrection;
            Vector3 rightOffset = worldRight * halfCorrection;
            leftTarget.SetWorldTranslation(leftPosition + leftOffset);
            rightTarget.SetWorldTranslation(rightPosition + rightOffset);
            leftTarget.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
            rightTarget.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
            RecordFeetSpacingCompensation(ELimbEndEffector.LeftFoot, leftOffset);
            RecordFeetSpacingCompensation(ELimbEndEffector.RightFoot, rightOffset);
        }

        private void RecordFeetSpacingCompensation(ELimbEndEffector goal, Vector3 offset)
        {
            int index = GetAnimatedGoalDiagnosticIndex(goal);
            if (index < 0)
                return;

            HumanoidIKGoalDiagnosticState previous = _animatedGoalDiagnostics[index];
            EHumanoidIKGoalApplicationStatus status = previous.ContactCompensationOffset == Vector3.Zero
                ? EHumanoidIKGoalApplicationStatus.AppliedWithFeetSpacing
                : EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensationAndFeetSpacing;
            _animatedGoalDiagnostics[index] = previous with
            {
                FeetSpacingCompensationOffset = offset,
                FinalWorldPosition = previous.FinalWorldPosition + offset,
                Status = status,
            };
        }

        private void RefreshAnimatedGoalTransform(ELimbEndEffector goal)
        {
            var target = GetAnimatedGoalTransform(goal);
            if (target is null)
                return;

            if (!ShouldUpdateAnimatedGoalTarget(goal))
                return;

            float scale = Humanoid.EstimateAnimatedMotionScale();
            Vector3 authoredLocalPosition = GetAnimatedGoalLocalPosition(goal);
            Vector3 localPosition = authoredLocalPosition * scale;
            Quaternion localRotation = GetAnimatedGoalLocalRotation(goal);
            Matrix4x4 bodyMatrix = GetAnimatedGoalBodyMatrix();
            Quaternion bodyRotation = GetAnimatedGoalBodyRotation();

            bool hasGoalRotationOffset = TryEnsureAnimatedGoalRotationOffset(goal);
            Quaternion goalRotationOffset = hasGoalRotationOffset
                ? GetAnimatedGoalRotationOffset(goal)
                : Quaternion.Identity;

            Quaternion worldRotation = hasGoalRotationOffset
                ? Quaternion.Normalize(bodyRotation * localRotation * goalRotationOffset)
                : Quaternion.Normalize(bodyRotation * localRotation);

            Vector3 bodyFrameWorldPosition = Vector3.Transform(localPosition, bodyMatrix);
            Vector3 contactOffset = CalculateContactCompensationOffset(goal, bodyFrameWorldPosition);
            Vector3 finalWorldPosition = bodyFrameWorldPosition + contactOffset;
            EHumanoidIKGoalApplicationStatus status = contactOffset == Vector3.Zero
                ? EHumanoidIKGoalApplicationStatus.AppliedAuthored
                : EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensation;

            int diagnosticIndex = GetAnimatedGoalDiagnosticIndex(goal);
            if (diagnosticIndex >= 0)
            {
                _animatedGoalDiagnostics[diagnosticIndex] = new HumanoidIKGoalDiagnosticState(
                    goal,
                    authoredLocalPosition,
                    localRotation,
                    bodyFrameWorldPosition,
                    worldRotation,
                    contactOffset,
                    Vector3.Zero,
                    finalWorldPosition,
                    worldRotation,
                    status);
            }

            Humanoid.SetIKTargetWorldPose(GetAnimatedGoalTarget(goal), finalWorldPosition, worldRotation);
            target.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
        }

        private Vector3 CalculateContactCompensationOffset(ELimbEndEffector goal, Vector3 worldPosition)
        {
            EHumanoidContactCompensationMode mode = Humanoid.Settings.ContactCompensationMode;
            bool eligible = mode switch
            {
                EHumanoidContactCompensationMode.GroundPlaneFeet
                    => goal is ELimbEndEffector.LeftFoot or ELimbEndEffector.RightFoot,
                EHumanoidContactCompensationMode.GroundPlaneFeetAndHands
                    => goal is ELimbEndEffector.LeftFoot
                    or ELimbEndEffector.RightFoot
                    or ELimbEndEffector.LeftHand
                    or ELimbEndEffector.RightHand,
                _ => false,
            };
            if (!eligible)
                return Vector3.Zero;

            float minimumY = Humanoid.Settings.ContactPlaneHeight + Humanoid.Settings.ContactClearance;
            float penetration = minimumY - worldPosition.Y;
            if (!float.IsFinite(penetration) || penetration <= 0.0f)
                return Vector3.Zero;

            return Vector3.UnitY * (penetration * Humanoid.Settings.ContactCompensationWeight);
        }

        private void RecordAnimatedGoalStatus(ELimbEndEffector goal, EHumanoidIKGoalApplicationStatus status)
        {
            int index = GetAnimatedGoalDiagnosticIndex(goal);
            if (index < 0)
                return;

            HumanoidIKGoalDiagnosticState previous = _animatedGoalDiagnostics[index];
            _animatedGoalDiagnostics[index] = previous with { Status = status };
        }

        private static int GetAnimatedGoalDiagnosticIndex(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => 0,
            ELimbEndEffector.RightFoot => 1,
            ELimbEndEffector.LeftHand => 2,
            ELimbEndEffector.RightHand => 3,
            _ => -1,
        };

        private bool TryEnsureAnimatedGoalRotationOffset(ELimbEndEffector goal)
        {
            if (HasAnimatedGoalRotationOffset(goal))
                return true;

            EHumanoidAvatarBoneRole role = GetGoalBoneRole(goal);
            if (!Humanoid.TryGetCompiledAvatarIKGoalRotationOffset(role, out Quaternion goalRotationOffset))
                return false;

            SetAnimatedGoalRotationOffset(goal, goalRotationOffset, initialized: true);
            return true;
        }

        private static EHumanoidAvatarBoneRole GetGoalBoneRole(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => EHumanoidAvatarBoneRole.LeftFoot,
            ELimbEndEffector.RightFoot => EHumanoidAvatarBoneRole.RightFoot,
            ELimbEndEffector.LeftHand => EHumanoidAvatarBoneRole.LeftHand,
            ELimbEndEffector.RightHand => EHumanoidAvatarBoneRole.RightHand,
            _ => EHumanoidAvatarBoneRole.Hips,
        };

        private Matrix4x4 GetAnimatedGoalBodyMatrix()
        {
            var body = Humanoid.Hips.Node?.GetTransformAs<Transform>(true);
            if (body is not null)
            {
                body.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
                return body.WorldMatrix;
            }

            var root = Root ?? Transform;
            root.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
            return root.WorldMatrix;
        }

        private Quaternion GetAnimatedGoalBodyRotation()
        {
            var body = Humanoid.Hips.Node?.GetTransformAs<Transform>(true);
            if (body is not null)
            {
                body.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
                return body.WorldRotation;
            }

            var root = Root ?? Transform;
            root.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
            return root.WorldRotation;
        }

        private Transform EnsureAnimatedGoalTransform(ELimbEndEffector goal)
        {
            var target = GetAnimatedGoalTransform(goal);
            if (target is not null)
                return target;

            var ik = GetGoalIK(goal);
            if (ik?.TargetIKTransform is Transform existingTarget)
            {
                Humanoid.SetIKTarget(GetAnimatedGoalTarget(goal), existingTarget, Matrix4x4.Identity);
                return existingTarget;
            }

            return Humanoid.EnsureOwnedIKTarget(GetAnimatedGoalTarget(goal), $"{goal}Target");
        }

        private Transform? GetAnimatedGoalTransform(ELimbEndEffector goal)
            => Humanoid.GetIKTargetTransform(GetAnimatedGoalTarget(goal)) as Transform;

        private static EHumanoidIKTarget GetAnimatedGoalTarget(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => EHumanoidIKTarget.LeftFoot,
            ELimbEndEffector.RightFoot => EHumanoidIKTarget.RightFoot,
            ELimbEndEffector.LeftHand => EHumanoidIKTarget.LeftHand,
            ELimbEndEffector.RightHand => EHumanoidIKTarget.RightHand,
            _ => EHumanoidIKTarget.LeftHand,
        };

        private bool ShouldUpdateAnimatedGoalTarget(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => UpdateLeftFootTarget,
            ELimbEndEffector.RightFoot => UpdateRightFootTarget,
            ELimbEndEffector.LeftHand => UpdateLeftHandTarget,
            ELimbEndEffector.RightHand => UpdateRightHandTarget,
            _ => true,
        };

        private Vector3 GetAnimatedGoalLocalPosition(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => _isStagingAnimatedGoalFrame ? GetStagedAnimatedGoalFrame(goal).Position : _animatedLeftFootLocalPosition,
            ELimbEndEffector.RightFoot => _isStagingAnimatedGoalFrame ? GetStagedAnimatedGoalFrame(goal).Position : _animatedRightFootLocalPosition,
            ELimbEndEffector.LeftHand => _isStagingAnimatedGoalFrame ? GetStagedAnimatedGoalFrame(goal).Position : _animatedLeftHandLocalPosition,
            ELimbEndEffector.RightHand => _isStagingAnimatedGoalFrame ? GetStagedAnimatedGoalFrame(goal).Position : _animatedRightHandLocalPosition,
            _ => Vector3.Zero,
        };

        private Quaternion GetAnimatedGoalLocalRotation(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => _isStagingAnimatedGoalFrame ? GetStagedAnimatedGoalFrame(goal).Rotation : _animatedLeftFootLocalRotation,
            ELimbEndEffector.RightFoot => _isStagingAnimatedGoalFrame ? GetStagedAnimatedGoalFrame(goal).Rotation : _animatedRightFootLocalRotation,
            ELimbEndEffector.LeftHand => _isStagingAnimatedGoalFrame ? GetStagedAnimatedGoalFrame(goal).Rotation : _animatedLeftHandLocalRotation,
            ELimbEndEffector.RightHand => _isStagingAnimatedGoalFrame ? GetStagedAnimatedGoalFrame(goal).Rotation : _animatedRightHandLocalRotation,
            _ => Quaternion.Identity,
        };

        private Quaternion GetAnimatedGoalRotationOffset(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => _animatedLeftFootGoalRotationOffset,
            ELimbEndEffector.RightFoot => _animatedRightFootGoalRotationOffset,
            ELimbEndEffector.LeftHand => _animatedLeftHandGoalRotationOffset,
            ELimbEndEffector.RightHand => _animatedRightHandGoalRotationOffset,
            _ => Quaternion.Identity,
        };

        private bool HasAnimatedGoalRotationOffset(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => _animatedLeftFootGoalRotationOffsetInitialized,
            ELimbEndEffector.RightFoot => _animatedRightFootGoalRotationOffsetInitialized,
            ELimbEndEffector.LeftHand => _animatedLeftHandGoalRotationOffsetInitialized,
            ELimbEndEffector.RightHand => _animatedRightHandGoalRotationOffsetInitialized,
            _ => false,
        };

        private void SetAnimatedGoalRotationOffset(ELimbEndEffector goal, Quaternion rotationOffset, bool initialized)
        {
            switch (goal)
            {
                case ELimbEndEffector.LeftFoot:
                    _animatedLeftFootGoalRotationOffset = rotationOffset;
                    _animatedLeftFootGoalRotationOffsetInitialized = initialized;
                    break;
                case ELimbEndEffector.RightFoot:
                    _animatedRightFootGoalRotationOffset = rotationOffset;
                    _animatedRightFootGoalRotationOffsetInitialized = initialized;
                    break;
                case ELimbEndEffector.LeftHand:
                    _animatedLeftHandGoalRotationOffset = rotationOffset;
                    _animatedLeftHandGoalRotationOffsetInitialized = initialized;
                    break;
                case ELimbEndEffector.RightHand:
                    _animatedRightHandGoalRotationOffset = rotationOffset;
                    _animatedRightHandGoalRotationOffsetInitialized = initialized;
                    break;
            }
        }

        private void ResetAnimatedGoalRotationOffsets()
        {
            ResetAnimatedGoalRotationOffset(ELimbEndEffector.LeftFoot);
            ResetAnimatedGoalRotationOffset(ELimbEndEffector.RightFoot);
            ResetAnimatedGoalRotationOffset(ELimbEndEffector.LeftHand);
            ResetAnimatedGoalRotationOffset(ELimbEndEffector.RightHand);
        }

        private void ResetAnimatedGoalRotationOffset(ELimbEndEffector goal)
            => SetAnimatedGoalRotationOffset(goal, Quaternion.Identity, initialized: false);

        private void SetAnimatedGoalLocalPosition(ELimbEndEffector goal, Vector3 position)
        {
            if (_isStagingAnimatedGoalFrame)
            {
                ref AnimatedGoalFrame frame = ref GetStagedAnimatedGoalFrame(goal);
                frame.Position = position;
                _stagedAnimatedGoalMask |= 1 << GetAnimatedGoalDiagnosticIndex(goal);
                return;
            }
            switch (goal)
            {
                case ELimbEndEffector.LeftFoot:
                    _animatedLeftFootLocalPosition = position;
                    break;
                case ELimbEndEffector.RightFoot:
                    _animatedRightFootLocalPosition = position;
                    break;
                case ELimbEndEffector.LeftHand:
                    _animatedLeftHandLocalPosition = position;
                    break;
                case ELimbEndEffector.RightHand:
                    _animatedRightHandLocalPosition = position;
                    break;
            }
        }

        private void SetAnimatedGoalLocalRotation(ELimbEndEffector goal, Quaternion rotation)
        {
            if (_isStagingAnimatedGoalFrame)
            {
                ref AnimatedGoalFrame frame = ref GetStagedAnimatedGoalFrame(goal);
                frame.Rotation = rotation;
                _stagedAnimatedGoalMask |= 1 << GetAnimatedGoalDiagnosticIndex(goal);
                return;
            }
            switch (goal)
            {
                case ELimbEndEffector.LeftFoot:
                    _animatedLeftFootLocalRotation = rotation;
                    break;
                case ELimbEndEffector.RightFoot:
                    _animatedRightFootLocalRotation = rotation;
                    break;
                case ELimbEndEffector.LeftHand:
                    _animatedLeftHandLocalRotation = rotation;
                    break;
                case ELimbEndEffector.RightHand:
                    _animatedRightHandLocalRotation = rotation;
                    break;
            }
        }

        private ref AnimatedGoalFrame GetStagedAnimatedGoalFrame(ELimbEndEffector goal)
            => ref _stagedAnimatedGoalFrames[GetAnimatedGoalDiagnosticIndex(goal)];

        private bool IsValidStagedAnimatedGoalFrame()
        {
            for (int i = 0; i < _stagedAnimatedGoalFrames.Length; i++)
            {
                if ((_stagedAnimatedGoalMask & (1 << i)) == 0)
                    continue;
                AnimatedGoalFrame frame = _stagedAnimatedGoalFrames[i];
                float rotationLengthSquared = frame.Rotation.LengthSquared();
                if (!float.IsFinite(frame.Position.X) || !float.IsFinite(frame.Position.Y) || !float.IsFinite(frame.Position.Z)
                    || !float.IsFinite(frame.Rotation.X) || !float.IsFinite(frame.Rotation.Y) || !float.IsFinite(frame.Rotation.Z) || !float.IsFinite(frame.Rotation.W)
                    || !float.IsFinite(rotationLengthSquared) || rotationLengthSquared <= 1e-8f
                    || !float.IsFinite(frame.PositionWeight) || !float.IsFinite(frame.RotationWeight)
                    || frame.PositionWeight is < 0.0f or > 1.0f || frame.RotationWeight is < 0.0f or > 1.0f)
                    return false;
            }
            return true;
        }

        private struct AnimatedGoalFrame(Vector3 position, Quaternion rotation, float positionWeight, float rotationWeight)
        {
            public Vector3 Position = position;
            public Quaternion Rotation = rotation;
            public float PositionWeight = positionWeight;
            public float RotationWeight = rotationWeight;
        }
    }
}
