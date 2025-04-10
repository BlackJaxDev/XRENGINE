using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Scene.Components.Animation
{
    /// <summary>
    /// Base abstract class for %IK solver components.
    /// </summary>
    public abstract class IKSolverComponent : BaseIKSolverComponent
    {
        /// <summary>
        /// Gets the solver used by this component.
        /// </summary>
        /// <returns></returns>
        public abstract IKSolver GetIKSolver();

        protected override void UpdateSolver()
        {
            var solver = GetIKSolver();

            if (!solver.Initialized)
                InitializeSolver();

            if (!solver.Initialized)
            {
                Debug.LogWarning("IK solver failed to initialize. Make sure the root Transform is set and the solver is valid.");
                return;
            }

            solver.Update();
        }

        protected override void InitializeSolver()
            => GetIKSolver().Initialize(SceneNode.GetTransformAs<Transform>());

        protected override void ResetTransformsToDefault()
            => GetIKSolver().ResetTransformToDefault();
    }
}
