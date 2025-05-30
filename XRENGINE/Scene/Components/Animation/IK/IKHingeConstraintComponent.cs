using Extensions;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// The hinge rotation limit limits the rotation to 1 degree of freedom around Axis. This rotation limit is additive which means the limits can exceed 360 degrees.
    /// </summary>
    public class IKHingeConstraintComponent : IKRotationConstraintComponent
    {
        /// <summary>
        /// Should the rotation be limited around the axis?
        /// </summary>
        public bool _useLimits = true;
        /// <summary>
        /// The min limit around the axis.
        /// </summary>
        public float _min = -45;
        /// <summary>
        /// The max limit around the axis.
        /// </summary>
        public float _max = 90;

        /// <summary>
        /// Limit the rotation of the hinge in local space.
        /// </summary>
        /// <param name="rotation"></param>
        /// <returns></returns>
        protected override Quaternion LimitRotation(Quaternion rotation)
        {
            return LimitHinge(rotation);
        }

        [HideInInspector]
        public float _zeroAxisDisplayOffset; // Angular offset of the scene view display of the Hinge rotation limit

        private float _lastAngle;

        private Quaternion LimitHinge(Quaternion rotation)
        {
            // If limit is zero return rotation fixed to axis
            if (_min == 0 && _max == 0 && _useLimits)
                return Quaternion.CreateFromAxisAngle(_axis, 0.0f);

            // Get 1 degree of freedom rotation along axis
            Quaternion free1DOF = Limit1DOF(rotation, _axis);
            if (!_useLimits)
                return free1DOF;

            Quaternion workingSpace = Quaternion.Inverse(Quaternion.CreateFromAxisAngle(_axis, _lastAngle) * XRMath.LookRotation(SecondaryAxis, _axis));
            Vector3 d = Vector3.Transform(SecondaryAxis, workingSpace * free1DOF);
            float deltaAngle = float.RadiansToDegrees(MathF.Atan2(d.X, d.Z));

            _lastAngle = (_lastAngle + deltaAngle).Clamp(_min, _max);
            return Quaternion.CreateFromAxisAngle(_axis, _lastAngle);
        }
    }
}
