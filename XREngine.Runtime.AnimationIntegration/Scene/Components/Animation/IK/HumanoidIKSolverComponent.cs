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
        // Capture a per-avatar offset from that goal basis into the actual wrist/foot
        // bone basis the first time an animated IK goal is evaluated.
        private Quaternion _animatedLeftFootGoalRotationOffset = Quaternion.Identity;
        private Quaternion _animatedRightFootGoalRotationOffset = Quaternion.Identity;
        private Quaternion _animatedLeftHandGoalRotationOffset = Quaternion.Identity;
        private Quaternion _animatedRightHandGoalRotationOffset = Quaternion.Identity;
        private bool _animatedLeftFootGoalRotationOffsetInitialized;
        private bool _animatedRightFootGoalRotationOffsetInitialized;
        private bool _animatedLeftHandGoalRotationOffsetInitialized;
        private bool _animatedRightHandGoalRotationOffsetInitialized;
        private bool _ikGoalWarningLogged;

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

                Engine.Rendering.Debug.RenderPoint(target.WorldTranslation, ColorF4.Green);
                if (limb._bone3._transform is not null)
                    Engine.Rendering.Debug.RenderLine(limb._bone3._transform.WorldTranslation, target.WorldTranslation, ColorF4.Green);
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

        public void SetIKPositionWeight(ELimbEndEffector goal, float weight)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            ik.IKPositionWeight = weight;
        }

        public void SetIKRotationWeight(ELimbEndEffector goal, float weight)
        {
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

        public void ClearAnimatedIKGoals()
        {
            ClearAnimatedIKGoal(ELimbEndEffector.LeftFoot);
            ClearAnimatedIKGoal(ELimbEndEffector.RightFoot);
            ClearAnimatedIKGoal(ELimbEndEffector.LeftHand);
            ClearAnimatedIKGoal(ELimbEndEffector.RightHand);
        }

        public void SetAnimatedIKPosition(ELimbEndEffector goal, Vector3 position)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            SetAnimatedGoalLocalPosition(goal, position);
            UpdateAnimatedIKGoal(goal);
        }

        public void SetAnimatedIKPositionX(ELimbEndEffector goal, float x)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            Vector3 position = GetAnimatedGoalLocalPosition(goal);
            position.X = x;
            SetAnimatedGoalLocalPosition(goal, position);
        }

        public void SetAnimatedIKPositionY(ELimbEndEffector goal, float y)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            Vector3 position = GetAnimatedGoalLocalPosition(goal);
            position.Y = y;
            SetAnimatedGoalLocalPosition(goal, position);
        }

        public void SetAnimatedIKPositionZ(ELimbEndEffector goal, float z)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            Vector3 position = GetAnimatedGoalLocalPosition(goal);
            position.Z = z;
            SetAnimatedGoalLocalPosition(goal, position);
            UpdateAnimatedIKGoal(goal);
        }

        public void SetAnimatedIKRotation(ELimbEndEffector goal, Quaternion rotation)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            SetAnimatedGoalLocalRotation(goal, rotation);
            UpdateAnimatedIKGoal(goal);
        }

        public void SetAnimatedIKRotationX(ELimbEndEffector goal, float x)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            Quaternion rotation = GetAnimatedGoalLocalRotation(goal);
            rotation.X = x;
            SetAnimatedGoalLocalRotation(goal, rotation);
        }

        public void SetAnimatedIKRotationY(ELimbEndEffector goal, float y)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            Quaternion rotation = GetAnimatedGoalLocalRotation(goal);
            rotation.Y = y;
            SetAnimatedGoalLocalRotation(goal, rotation);
        }

        public void SetAnimatedIKRotationZ(ELimbEndEffector goal, float z)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            Quaternion rotation = GetAnimatedGoalLocalRotation(goal);
            rotation.Z = z;
            SetAnimatedGoalLocalRotation(goal, rotation);
        }

        public void SetAnimatedIKRotationW(ELimbEndEffector goal, float w)
        {
            if (!ShouldApplyAnimatedIKGoal())
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

        protected override void ResetTransformsToDefault()
        {
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
            RefreshAnimatedGoalTransforms(captureRotationOffsets: true);

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

        private bool ShouldApplyAnimatedIKGoal()
        {
            switch (Humanoid.Settings.IKGoalPolicy)
            {
                case EHumanoidIKGoalPolicy.AlwaysApply:
                    return true;
                case EHumanoidIKGoalPolicy.ApplyIfCalibrated:
                    if (Humanoid.Settings.IsIKCalibrated)
                        return true;

                    if (!_ikGoalWarningLogged)
                    {
                        _ikGoalWarningLogged = true;
                        Debug.Animation("[HumanoidIKSolverComponent] IK goal channels present but avatar is not calibrated; skipping animation-driven IK goals.");
                    }
                    return false;
                default:
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

            RefreshAnimatedGoalTransform(goal, captureRotationOffset: false);
        }

        private void RefreshAnimatedGoalTransforms(bool captureRotationOffsets)
        {
            RefreshAnimatedGoalTransform(ELimbEndEffector.LeftFoot, captureRotationOffsets);
            RefreshAnimatedGoalTransform(ELimbEndEffector.RightFoot, captureRotationOffsets);
            RefreshAnimatedGoalTransform(ELimbEndEffector.LeftHand, captureRotationOffsets);
            RefreshAnimatedGoalTransform(ELimbEndEffector.RightHand, captureRotationOffsets);
        }

        private void RefreshAnimatedGoalTransform(ELimbEndEffector goal, bool captureRotationOffset)
        {
            var target = GetAnimatedGoalTransform(goal);
            if (target is null)
                return;

            if (!ShouldUpdateAnimatedGoalTarget(goal))
                return;

            float scale = Humanoid.EstimateAnimatedMotionScale();
            Vector3 localPosition = GetAnimatedGoalLocalPosition(goal) * scale;
            Quaternion localRotation = GetAnimatedGoalLocalRotation(goal);
            Matrix4x4 bodyMatrix = GetAnimatedGoalBodyMatrix();
            Quaternion bodyRotation = GetAnimatedGoalBodyRotation();

            Quaternion goalRotationOffset = captureRotationOffset
                ? EnsureAnimatedGoalRotationOffset(goal, bodyRotation, localRotation)
                : GetAnimatedGoalRotationOffset(goal);

            Quaternion worldRotation = HasAnimatedGoalRotationOffset(goal)
                ? Quaternion.Normalize(bodyRotation * localRotation * goalRotationOffset)
                : Quaternion.Normalize(bodyRotation * localRotation);

            Humanoid.SetIKTargetWorldPose(GetAnimatedGoalTarget(goal), Vector3.Transform(localPosition, bodyMatrix), worldRotation);
            target.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
        }

        private Quaternion EnsureAnimatedGoalRotationOffset(ELimbEndEffector goal, Quaternion bodyRotation, Quaternion localRotation)
        {
            if (HasAnimatedGoalRotationOffset(goal))
                return GetAnimatedGoalRotationOffset(goal);

            var goalBone = GetGoalBoneTransform(goal);
            if (goalBone is null)
                return Quaternion.Identity;

            // Match the imported first-frame goal rotation to the avatar's current
            // wrist/foot bone orientation, then preserve subsequent delta motion.
            Quaternion importedWorldRotation = Quaternion.Normalize(bodyRotation * localRotation);
            Quaternion goalRotationOffset = Quaternion.Normalize(Quaternion.Inverse(importedWorldRotation) * goalBone.WorldRotation);
            SetAnimatedGoalRotationOffset(goal, goalRotationOffset, initialized: true);
            return goalRotationOffset;
        }

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

        private Transform? GetGoalBoneTransform(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => Humanoid.Left.Foot.Node?.GetTransformAs<Transform>(true),
            ELimbEndEffector.RightFoot => Humanoid.Right.Foot.Node?.GetTransformAs<Transform>(true),
            ELimbEndEffector.LeftHand => Humanoid.Left.Wrist.Node?.GetTransformAs<Transform>(true),
            ELimbEndEffector.RightHand => Humanoid.Right.Wrist.Node?.GetTransformAs<Transform>(true),
            _ => null,
        };

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
            ELimbEndEffector.LeftFoot => _animatedLeftFootLocalPosition,
            ELimbEndEffector.RightFoot => _animatedRightFootLocalPosition,
            ELimbEndEffector.LeftHand => _animatedLeftHandLocalPosition,
            ELimbEndEffector.RightHand => _animatedRightHandLocalPosition,
            _ => Vector3.Zero,
        };

        private Quaternion GetAnimatedGoalLocalRotation(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => _animatedLeftFootLocalRotation,
            ELimbEndEffector.RightFoot => _animatedRightFootLocalRotation,
            ELimbEndEffector.LeftHand => _animatedLeftHandLocalRotation,
            ELimbEndEffector.RightHand => _animatedRightHandLocalRotation,
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
    }
}
