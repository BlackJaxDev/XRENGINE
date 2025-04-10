using Extensions;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    public static partial class InverseKinematics
    {
        /// <summary>
        /// Contains information to solve for a bone in an IK chain.
        /// </summary>
        /// <param name="node"></param>
        /// <param name="constraints"></param>
        public class BoneChainItem(SceneNode node, IKRotationConstraintComponent? constraints = null) : XRBase
        {
            public static implicit operator BoneChainItem(SceneNode def) => new(def);

            public SceneNode? Node => node;
            public Transform? Transform => Node?.GetTransformAs<Transform>(true);

            public IKRotationConstraintComponent? Constraints
            {
                get => constraints;
                set => SetField(ref constraints, value);
            }

            private float _distanceToChild;
            /// <summary>
            /// Distance to the next child bone in the chain.
            /// This is not changed.
            /// </summary>
            public float DistanceToChild
            {
                get => _distanceToChild;
                set => SetField(ref _distanceToChild, value);
            }

            private Vector3 _worldChildDirSolve;
            /// <summary>
            /// The normalized direction of the bone that points to the next bone (child) in the chain.
            /// This willd be updated during iteration to face the target direction.
            /// </summary>
            public Vector3 WorldChildDirSolve
            {
                get => _worldChildDirSolve;
                set => SetField(ref _worldChildDirSolve, value);
            }

            private Vector3 _worldPosSolve = Vector3.Zero;
            /// <summary>
            /// The world position of the bone that gets copied from the transform.
            /// This will be updated during iterations, and then used to update the transform later with rotations.
            /// </summary>
            public Vector3 WorldPosSolve
            {
                get => _worldPosSolve;
                set => SetField(ref _worldPosSolve, value);
            }

            /// <summary>
            /// Helper method to initialize the bone chain item to point to the next bone in the chain.
            /// </summary>
            /// <param name="childWorldPosition"></param>
            /// <returns></returns>
            public void SetWorldChildDirTo(Vector3 childWorldPosition)
            {
                WorldChildDirSolve = (childWorldPosition - WorldPosSolve).Normalized();
            }
        }
    }
}
