using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    public abstract partial class IKSolver
    {
        /// <summary>
        /// %Node type of element in the %IK chain. Used in the case of mixed/non-hierarchical %IK systems
        /// </summary>
        [System.Serializable]
        public class IKNode : IKPoint
        {
            /// <summary>
            /// Distance to child node.
            /// </summary>
            public float _length;
            /// <summary>
            /// The effector position weight.
            /// </summary>
            public float _effectorPositionWeight;
            /// <summary>
            /// The effector rotation weight.
            /// </summary>
            public float _effectorRotationWeight;
            /// <summary>
            /// Position offset.
            /// </summary>
            public Vector3 _offset;

            public IKNode() { }

            public IKNode(Transform transform)
            {
                _transform = transform;
            }

            public IKNode(Transform transform, float weight)
            {
                _transform = transform;
                _weight = weight;
            }
        }
    }
}
