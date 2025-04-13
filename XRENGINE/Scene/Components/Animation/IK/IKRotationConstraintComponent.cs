using Extensions;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    /// <summary>
    /// Specifies constraints for a scene node's in an IK bone chain.
    /// </summary>
    public abstract class IKRotationConstraintComponent : XRComponent
    {
        /// <summary>
        /// The main axis of the rotation limit.
        /// </summary>
        protected Vector3 _axis = Globals.Forward;
        public Vector3 Axis
        {
            get => _axis;
            set
            {
                if (_axis == value)
                    return;
                _axis = value;
                _initiated = false;
            }
        }

        /// <summary>
        /// Map the zero rotation point to the current local rotation of this gameobject.
        /// </summary>
        public void SetDefaultLocalRotation()
        {
            _defaultLocalRotation = Transform.LocalRotation;
            _defaultLocalRotationSet = true;
            DefaultLocalRotationOverride = false;
        }

        /// <summary>
		/// Map the zero rotation point to the specified rotation.
		/// </summary>
        public void SetDefaultLocalRotation(Quaternion localRotation)
        {
            _defaultLocalRotation = localRotation;
            _defaultLocalRotationSet = true;
            DefaultLocalRotationOverride = true;
        }

        public Quaternion GetLimitedLocalRotation(Quaternion localRotation, out bool changed)
        {
            if (!_initiated)
                Initiate();

            //Remove defaultLocalRotation
            Quaternion rotation = Quaternion.Inverse(_defaultLocalRotation) * localRotation;

            Quaternion limitedRotation = LimitRotation(rotation);
            limitedRotation = Quaternion.Normalize(limitedRotation);
            changed = limitedRotation != rotation;

            if (!changed)
                return localRotation;

            //Add defaultLocalRotation
            return _defaultLocalRotation * limitedRotation;
        }

        /// <summary>
        /// Applies the rotation limit to this transform.
        /// Returns true if the limit took effect.
        /// </summary>
        public bool Apply()
        {
            var tfm = SceneNode.GetTransformAs<Transform>(true)!;
            tfm.Rotation = GetLimitedLocalRotation(tfm.Rotation, out bool changed);
            return changed;
        }

        /// <summary>
        /// Arbitrary axis made by swapping the components of the main axis.
        /// </summary>
        public Vector3 SecondaryAxis => new(_axis.Y, _axis.Z, _axis.X);

        /// <summary>
        /// The crossed axis of the main axis and the secondary axis.
        /// </summary>
        public Vector3 CrossAxis => Vector3.Cross(_axis, SecondaryAxis);

        public Quaternion _defaultLocalRotation;

        public bool DefaultLocalRotationOverride { get; private set; }

        protected abstract Quaternion LimitRotation(Quaternion rotation);

        private bool _initiated;
        private bool _defaultLocalRotationSet;

        void Initiate()
        {
            // Store the local rotation to map the zero rotation point to the current rotation
            if (!_defaultLocalRotationSet)
                SetDefaultLocalRotation();

            if (_axis == Vector3.Zero)
            {
                Debug.LogWarning("Axis is Vector3.zero. Defaulting to Globals.Forward.");
                _axis = Globals.Forward;
            }

            _initiated = true;
        }

        void LateUpdate()
        {
            Apply();
        }

        /// <summary>
        /// Limits the rotation to 1 degree of freedom around the axis.
        /// </summary>
        /// <param name="rotation"></param>
        /// <param name="axis"></param>
        /// <returns></returns>
        protected static Quaternion Limit1DOF(Quaternion rotation, Vector3 axis)
            => XRMath.RotationBetweenVectors(Vector3.Transform(axis, rotation), axis) * rotation;

        protected static Quaternion LimitTwist(Quaternion rotation, Vector3 axis, Vector3 orthoAxis, float twistLimit)
        {
            twistLimit = twistLimit.Clamp(0, 180);
            if (twistLimit >= 180) return rotation;

            Vector3 normal = rotation.Rotate(axis);
            Vector3 orthoTangent = orthoAxis;
            XRMath.OrthoNormalize(ref normal, ref orthoTangent);

            Vector3 rotatedOrthoTangent = rotation.Rotate(orthoAxis);
            XRMath.OrthoNormalize(ref normal, ref rotatedOrthoTangent);

            Quaternion fixedRotation = XRMath.RotationBetweenVectors(rotatedOrthoTangent, orthoTangent) * rotation;

            if (twistLimit <= 0)
                return fixedRotation;

            // Rotate from zero twist to free twist by the limited angle
            return XRMath.RotateTowards(fixedRotation, rotation, twistLimit);
        }

        /*
		 * Returns the angle between two vectors on a plane with the specified normal
		 * */
        protected static float GetOrthogonalAngle(Vector3 v1, Vector3 v2, Vector3 normal)
        {
            XRMath.OrthoNormalize(ref normal, ref v1);
            XRMath.OrthoNormalize(ref normal, ref v2);
            return XRMath.GetBestAngleDegreesBetween(v1, v2);
        }
    }
}
