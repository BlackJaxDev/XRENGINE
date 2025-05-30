using Extensions;
using System.Numerics;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Controls the root motion of a character with VRIK by using target transforms.
    /// This component manages the relationship between IK targets (hips, feet) and the root transform,
    /// allowing the character to maintain proper positioning in the virtual world.
    /// </summary>
    [RequireComponents(typeof(VRIKSolverComponent))]
    public class VRIKRootControllerComponent : XRComponent
    {
        /// <summary>
        /// Reference to the VRIK solver component that this controller will manipulate.
        /// </summary>
        public VRIKSolverComponent IKSolverComponent => GetSiblingComponent<VRIKSolverComponent>(true)!;

        /// <summary>
        /// The right vector of the pelvis target in local space, used for orientation calculations.
        /// Set to the root's world right normal rotated by the hips' world rotation by default.
        /// </summary>
        public Vector3 HipsTargetRight { get; private set; }

        /// <summary>
        /// Reference to the hips target transform used by the IK solver.
        /// </summary>
        private Transform? _hipsTarget;

        /// <summary>
        /// Reference to the left foot target transform used by the IK solver.
        /// </summary>
        private Transform? _leftFootTarget;

        /// <summary>
        /// Reference to the right foot target transform used by the IK solver.
        /// </summary>
        private Transform? _rightFootTarget;

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            IKSolverComponent.Solver.OnPreUpdate += OnPreUpdate;
            Calibrate();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            IKSolverComponent.Solver.OnPreUpdate -= OnPreUpdate;
        }

        /// <summary>
        /// Calibrates the controller by getting references to the IK targets and calculating the pelvis orientation.
        /// </summary>
        public void Calibrate()
        {
            if (IKSolverComponent is null)
            {
                Debug.LogWarning("No VRIK found on VRIKRootController's GameObject.");
                return;
            }

            var solver = IKSolverComponent.Solver;
            var root = IKSolverComponent.Root;

            _hipsTarget = solver.Spine.HipsTarget;
            _leftFootTarget = solver.LeftLeg.Target;
            _rightFootTarget = solver.RightLeg.Target;

            if (_hipsTarget != null && root != null)
                HipsTargetRight = Quaternion.Inverse(_hipsTarget.WorldRotation).Rotate(root.WorldRight);
        }

        /// <summary>
        /// Calibrates the controller using pre-calculated calibration data.
        /// </summary>
        /// <param name="data">The calibration data to use for setup.</param>
        public void Calibrate(VRIKCalibrator.CalibrationData data)
        {
            var solverComp = IKSolverComponent;
            if (solverComp is null)
            {
                Debug.LogWarning("No VRIK found on VRIKRootController's GameObject.");
                return;
            }
            var solver = solverComp.Solver;

            _hipsTarget = solver.Spine.HipsTarget;
            _leftFootTarget = solver.LeftLeg.Target;
            _rightFootTarget = solver.RightLeg.Target;
            if (_hipsTarget != null)
                HipsTargetRight = data.HipsTargetRight;
        }

        /// <summary>
        /// Called before the VRIK solver updates. Handles positioning the root transform based on IK targets.
        /// </summary>
        private void OnPreUpdate()
        {
            if (!IsActiveInHierarchy)
                return;

            var solverComp = IKSolverComponent;
            var root = solverComp.Root;
            if (root is null)
            {
                Debug.LogWarning("Can not update VRIKRootController without the root transform.");
                return;
            }

            var hipsTfm = solverComp.Humanoid?.Hips.Node?.GetTransformAs<Transform>(true);
            if (_hipsTarget != null && hipsTfm != null)
            {
                // Position the root at the hips target's X/Z position, preserving the current Y height
                //root.SetWorldTranslation(new Vector3(
                //    _hipsTarget.WorldTranslation.X,
                //    root.WorldTranslation.Y,
                //    _hipsTarget.WorldTranslation.Z));

                // Calculate the forward direction by crossing the hips right vector with the world up
                Vector3 f = Vector3.Cross(_hipsTarget.WorldRotation.Rotate(HipsTargetRight), root.WorldUp);
                f.Y = 0f; // Ensure the forward vector is horizontal
                root.SetWorldRotation(XRMath.LookRotation(f));
                root.RecalculateMatrices(true);

                // Interpolate the hips position and rotation based on solver weights
                hipsTfm.SetWorldTranslationRotation(
                    Vector3.Lerp(hipsTfm.WorldTranslation, _hipsTarget.WorldTranslation, solverComp.Solver.Spine.HipsPositionWeight),
                    Quaternion.Slerp(hipsTfm.WorldRotation, _hipsTarget.WorldRotation, solverComp.Solver.Spine.HipsRotationWeight));
                hipsTfm.RecalculateMatrices(true);
            }
            else if (_leftFootTarget != null && _rightFootTarget != null)
            {
                // If no hips target is available, position the root at the midpoint between feet
                //root.SetWorldTranslation(Vector3.Lerp(_leftFootTarget.WorldTranslation, _rightFootTarget.WorldTranslation, 0.5f));
            }
        }

        /// <summary>
        /// Called when the component is being destroyed. Unsubscribes from solver events.
        /// </summary>
        protected override void OnDestroying()
        {
            if (IKSolverComponent != null)
                IKSolverComponent.Solver.OnPreUpdate -= OnPreUpdate;

            base.OnDestroying();
        }
    }
}
