using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    public abstract partial class IKSolver
    {
        /// <summary>
        /// %Bone type of element in the %IK chain. Used in the case of skeletal Transform hierarchies.
        /// </summary>
        [System.Serializable]
        public class IKBone : IKPoint
        {
            /// <summary>
            /// The length of the bone.
            /// </summary>
            public float _length;
            /// <summary>
            /// The sqr mag of the bone.
            /// </summary>
            public float _lengthSquared;
            /// <summary>
            /// Local axis to target/child bone.
            /// </summary>
            public Vector3 _axis = -Globals.Right;

            /// <summary>
            /// Gets the rotation limit component from the Transform if there is any.
            /// </summary>
            public IKRotationConstraintComponent? RotationLimit
            {
                get
                {
                    if (!_isLimited)
                        return null;

                    _rotationLimit ??= _transform?.SceneNode?.GetComponent<IKRotationConstraintComponent>();

                    _isLimited = _rotationLimit != null;

                    return _rotationLimit;
                }
                set
                {
                    _rotationLimit = value;
                    _isLimited = value != null;
                }
            }

            public void Swing(Vector3 swingTarget, float weight = 1f)
            {
                if (_transform is null)
                    return;

                if (weight <= 0f)
                    return;

                var current = Vector3.Transform(_axis, _transform.WorldRotation);
                var target = swingTarget - _transform.WorldTranslation;
                Quaternion deltaRot = XRMath.RotationBetweenVectors(current, target);
                _transform.AddWorldRotationDelta(Quaternion.Lerp(Quaternion.Identity, deltaRot, weight));
            }

            public static void SolverSwing(IKBone[] bones, int index, Vector3 swingTarget, float weight = 1f)
            {
                if (weight <= 0f)
                    return;

                var current = Vector3.Transform(bones[index]._axis, bones[index]._solverRotation);
                var target = swingTarget - bones[index]._solverPosition;
                Quaternion r = XRMath.RotationBetweenVectors(current, target);

                if (weight >= 1.0f)
                {
                    for (int i = index; i < bones.Length; i++)
                        bones[i]._solverRotation = r * bones[i]._solverRotation;
                    return;
                }

                for (int i = index; i < bones.Length; i++)
                    bones[i]._solverRotation = Quaternion.Lerp(Quaternion.Identity, r, weight) * bones[i]._solverRotation;
            }

            public void Swing2D(Vector3 swingTarget, float weight = 1f)
            {
                if (_transform is null)
                    return;

                if (weight <= 0f)
                    return;

                Vector3 from = Vector3.Transform(_axis, _transform.WorldRotation);
                Vector3 to = swingTarget - _transform.WorldTranslation;

                float angleFrom = float.RadiansToDegrees(MathF.Atan2(from.X, from.Y));
                float angleTo = float.RadiansToDegrees(MathF.Atan2(to.X, to.Y));
                
                _transform.SetWorldRotation(Quaternion.CreateFromAxisAngle(Globals.Forward, XRMath.DeltaAngle(angleFrom, angleTo) * weight) * _transform.WorldRotation);
            }

            public void SetToSolverPosition()
            {
                _transform?.SetWorldTranslation(_solverPosition);
            }

            public IKBone() { }

            public IKBone(Transform transform)
            {
                _transform = transform;
            }

            public IKBone(Transform transform, float weight)
            {
                _transform = transform;
                _weight = weight;
            }

            private IKRotationConstraintComponent? _rotationLimit;
            private bool _isLimited = true;
        }
    }
}
