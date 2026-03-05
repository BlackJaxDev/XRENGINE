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
    public class HumanoidIKSolverComponent : BaseIKSolverComponent
    {
        private const float FullGoalWeight = 1.0f;
        public HumanoidComponent Humanoid => GetSiblingComponent<HumanoidComponent>(true)!;

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
        private SceneNode? _animatedGoalRootNode;
        private Transform? _animatedLeftFootTarget;
        private Transform? _animatedRightFootTarget;
        private Transform? _animatedLeftHandTarget;
        private Transform? _animatedRightHandTarget;
        private Vector3 _animatedLeftFootLocalPosition;
        private Vector3 _animatedRightFootLocalPosition;
        private Vector3 _animatedLeftHandLocalPosition;
        private Vector3 _animatedRightHandLocalPosition;
        private Quaternion _animatedLeftFootLocalRotation = Quaternion.Identity;
        private Quaternion _animatedRightFootLocalRotation = Quaternion.Identity;
        private Quaternion _animatedLeftHandLocalRotation = Quaternion.Identity;
        private Quaternion _animatedRightHandLocalRotation = Quaternion.Identity;
        private bool _ikGoalWarningLogged;

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

        public void SetAnimatedIKRotation(ELimbEndEffector goal, Quaternion rotation)
        {
            if (!ShouldApplyAnimatedIKGoal())
                return;

            SetAnimatedGoalLocalRotation(goal, rotation);
            UpdateAnimatedIKGoal(goal);
        }

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

            var rootTfm = SceneNode.GetTransformAs<Transform>(true)!;

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
        }

        private void UpdateAnimatedIKGoal(ELimbEndEffector goal)
        {
            var ik = GetGoalIK(goal);
            if (ik is null)
                return;

            var target = EnsureAnimatedGoalTransform(goal);
            ik.TargetIKTransform = target;

            float scale = EstimateAnimatedGoalScale();
            Vector3 localPosition = GetAnimatedGoalLocalPosition(goal) * scale;
            Quaternion localRotation = GetAnimatedGoalLocalRotation(goal);
            Matrix4x4 bodyMatrix = GetAnimatedGoalBodyMatrix();
            Quaternion bodyRotation = GetAnimatedGoalBodyRotation();

            target.SetWorldTranslation(Vector3.Transform(localPosition, bodyMatrix));
            target.SetWorldRotation(Quaternion.Normalize(bodyRotation * localRotation));
        }

        private Matrix4x4 GetAnimatedGoalBodyMatrix()
            => Humanoid.Hips.Node?.GetTransformAs<Transform>(true)?.WorldMatrix ?? Transform.WorldMatrix;

        private Quaternion GetAnimatedGoalBodyRotation()
            => Humanoid.Hips.Node?.GetTransformAs<Transform>(true)?.WorldRotation ?? Transform.WorldRotation;

        private float EstimateAnimatedGoalScale()
        {
            Vector3 hips = Humanoid.Hips.WorldBindPose.Translation;
            float total = 0.0f;
            int count = 0;

            if (Humanoid.Left.Foot.Node is not null)
            {
                float left = Vector3.Distance(hips, Humanoid.Left.Foot.WorldBindPose.Translation);
                if (left > 0.0001f)
                {
                    total += left;
                    count++;
                }
            }

            if (Humanoid.Right.Foot.Node is not null)
            {
                float right = Vector3.Distance(hips, Humanoid.Right.Foot.WorldBindPose.Translation);
                if (right > 0.0001f)
                {
                    total += right;
                    count++;
                }
            }

            if (count > 0)
                return total / count;

            float fallback = Transform.LossyWorldScale.Y;
            return fallback > 0.0001f ? fallback : 1.0f;
        }

        private Transform EnsureAnimatedGoalTransform(ELimbEndEffector goal)
        {
            // First check the cached animated target
            var target = GetAnimatedGoalTransform(goal);
            if (target is not null)
                return target;

            // Reuse an existing TargetIKTransform if one was already assigned
            // (e.g. by external setup like AddCharacterIK) instead of creating a duplicate.
            var ik = GetGoalIK(goal);
            if (ik?.TargetIKTransform is Transform existingTarget)
            {
                SetAnimatedGoalTransform(goal, existingTarget);
                return existingTarget;
            }

            _animatedGoalRootNode ??= SceneNode.NewChild("AnimatedIKTargets");
            var targetNode = _animatedGoalRootNode.NewChild($"{goal}Target");
            target = targetNode.GetTransformAs<Transform>(true)!;
            SetAnimatedGoalTransform(goal, target);
            return target;
        }

        private void SetAnimatedGoalTransform(ELimbEndEffector goal, Transform target)
        {
            switch (goal)
            {
                case ELimbEndEffector.LeftFoot:
                    _animatedLeftFootTarget = target;
                    break;
                case ELimbEndEffector.RightFoot:
                    _animatedRightFootTarget = target;
                    break;
                case ELimbEndEffector.LeftHand:
                    _animatedLeftHandTarget = target;
                    break;
                case ELimbEndEffector.RightHand:
                    _animatedRightHandTarget = target;
                    break;
            }
        }

        private Transform? GetAnimatedGoalTransform(ELimbEndEffector goal) => goal switch
        {
            ELimbEndEffector.LeftFoot => _animatedLeftFootTarget,
            ELimbEndEffector.RightFoot => _animatedRightFootTarget,
            ELimbEndEffector.LeftHand => _animatedLeftHandTarget,
            ELimbEndEffector.RightHand => _animatedRightHandTarget,
            _ => null,
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
