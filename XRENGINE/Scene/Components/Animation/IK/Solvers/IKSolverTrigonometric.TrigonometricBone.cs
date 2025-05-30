using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Components.Animation
{
    public partial class IKSolverTrigonometric
    {
        /// <summary>
        /// Bone type used by IKSolverTrigonometric.
        /// </summary>
        [Serializable]
        public class TrigonometricBone : IKSolver.IKBone
        {
            private Quaternion _targetToLocalSpace;
            private Vector3 _defaultLocalBendNormal;

            public void Initialize(Vector3 childPosition, Vector3 bendNormal)
            {
                if (_transform is null)
                    return;

                // Get default target rotation that looks at child position with bendNormal as up
                Quaternion defaultTargetRotation = XRMath.LookRotation(childPosition - _transform.WorldTranslation, bendNormal);

                // Covert default target rotation to local space
                _targetToLocalSpace = XRMath.RotationToLocalSpace(_transform.WorldRotation, defaultTargetRotation);

                _defaultLocalBendNormal = Vector3.Transform(bendNormal, Quaternion.Inverse(_transform.WorldRotation));
            }

            public Quaternion GetRotation(Vector3 direction, Vector3 bendNormal)
                => XRMath.LookRotation(direction, bendNormal) * _targetToLocalSpace;

            public Vector3 GetBendNormalFromCurrentRotation()
                => _transform is null ? _defaultLocalBendNormal : Vector3.Transform(_defaultLocalBendNormal, _transform.WorldRotation);
        }
    }
}
