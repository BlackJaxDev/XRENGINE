using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Core.Attributes;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
    [RequireComponents(typeof(HumanoidComponent))]
    public class HumanoidIKSolverComponent : BaseIKSolverComponent
    {
        public HumanoidComponent Humanoid => GetSiblingComponent<HumanoidComponent>(true)!;

        public IKSolverLimb _leftFoot = new(ELimbEndEffector.LeftFoot) { _bendModifier = ELimbBendModifier.Target };
        public IKSolverLimb _rightFoot = new(ELimbEndEffector.RightFoot) { _bendModifier = ELimbBendModifier.Target };
        public IKSolverLimb _leftHand = new(ELimbEndEffector.LeftHand) { _bendModifier = ELimbBendModifier.Arm };
        public IKSolverLimb _rightHand = new(ELimbEndEffector.RightHand) { _bendModifier = ELimbBendModifier.Arm };
        public IKSolverFABRIK _spine = new();
        //public IKSolverLookAt lookAt = new IKSolverLookAt();
        //public IKSolverAim aim = new IKSolverAim();
        public TransformConstrainer _hips = new();

        public override void Visualize()
        {
            //_hips.Visualize();
            //_leftFoot.Visualize();
            //_rightFoot.Visualize();
            //_leftHand.Visualize();
            //_rightHand.Visualize();
            //_spine.Visualize();
            //lookAt.Visualize();
            //aim.Visualize();
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
    }
}
