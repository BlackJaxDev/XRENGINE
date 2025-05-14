using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    /// <summary>
    /// Component that uses the VRIK solver to solve IK for a humanoid character controlled by a VR headset, controllers, and optional trackers.
    /// </summary>
    [RequireComponents(typeof(HumanoidComponent))]
    public class VRIKSolverComponent : IKSolverComponent
    {
        public IKSolverVR Solver { get; } = new();
        public HumanoidComponent Humanoid => GetSiblingComponent<HumanoidComponent>(true)!;
        public Transform? Root => Humanoid?.SceneNode?.GetTransformAs<Transform>(true);

        /// <summary>
        /// Fills in arm wristToPalmAxis and palmToThumbAxis.
        /// </summary>
        public void GuessHandOrientations()
            => Solver.GuessHandOrientations(Humanoid, false);

        public override IKSolver GetIKSolver() => Solver;

        protected override void InitializeSolver()
        {
            Solver.SetToReferences(Humanoid);
            base.InitializeSolver();
        }

        protected override void UpdateSolver()
        {
            if (!(Humanoid?.SceneNode?.IsTransformNull ?? true) && Humanoid.SceneNode.Transform.LossyWorldScale.LengthSquared() < float.Epsilon)
            {
                Debug.LogWarning("VRIK Root Transform's scale is zero, can not update VRIK. Make sure you have not calibrated the character to a zero scale.");
                IsActive = false;
                return;
            }

            base.UpdateSolver();
        }
    }
}
