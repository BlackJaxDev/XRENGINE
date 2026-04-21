using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Constraints a transform's position and rotation to either
    /// a target transform,
    /// a specified position and rotation,
    /// a specified delta position and rotation,
    /// or a combination.
    /// </summary>
    [Serializable]
    public class TransformConstrainer : XRBase
    {
        private Transform? _transform;
        private Transform? _target;
        private Vector3 _positionOffset;
        private Vector3 _position;
        private float _positionWeight;
        private Vector3 _rotationOffset;
        private Vector3 _rotation;
        private float _rotationWeight;

        private Vector3 _defaultLocalPosition;
        private Quaternion _defaultLocalRotation;

        /// <summary>
        /// The transform to constrain.
        /// </summary>
        public Transform? Transform
        {
            get => _transform;
            set => SetField(ref _transform, value);
        }

        /// <summary>
        /// The target for the transform to follow.
        /// </summary>
        public Transform? Target
        {
            get => _target;
            set => SetField(ref _target, value);
        }

        /// <summary>
        /// The position offset to apply to the transform.
        /// </summary>
        public Vector3 PositionOffset
        {
            get => _positionOffset;
            set => SetField(ref _positionOffset, value);
        }

        /// <summary>
        /// The position to move the transform to, weighted by PositionWeight.
        /// </summary>
        public Vector3 Position
        {
            get => _position;
            set => SetField(ref _position, value);
        }

        /// <summary>
        /// The weight of the constaint's effect on the transform's position.
        /// </summary>
        [Range(0.0f, 1.0f)]
        public float PositionWeight
        {
            get => _positionWeight;
            set => SetField(ref _positionWeight, value);
        }

        /// <summary>
        /// The rotation offset to apply to the transform.
        /// </summary>
        public Vector3 RotationOffset
        {
            get => _rotationOffset;
            set => SetField(ref _rotationOffset, value);
        }

        /// <summary>
        /// The rotation to move the transform to, weighted by RotationWeight.
        /// </summary>
        public Vector3 Rotation
        {
            get => _rotation;
            set => SetField(ref _rotation, value);
        }

        /// <summary>
        /// The weight of the constaint's effect on the transform's rotation.
        /// </summary>
        [Range(0.0f, 1.0f)]
        public float RotationWeight 
        {
            get => _rotationWeight;
            set => SetField(ref _rotationWeight, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Transform):
                    if (_transform is not null)
                    {
                        Position = _transform.WorldTranslation;
                        Rotation = XRMath.QuaternionToEuler(_transform.WorldRotation);

                        _defaultLocalPosition = _transform.Translation;
                        _defaultLocalRotation = _transform.Rotation;
                    }
                    break;
            }
        }

        public void ResetTransformToDefault()
        {
            if (Transform is null)
                return;

            Transform.Translation = _defaultLocalPosition;
            Transform.Rotation = _defaultLocalRotation;
        }

        /// <summary>
        /// Updates the constraints.
        /// </summary>
        public void Update()
        {
            if (Transform is null)
                return;

            if (Target != null)
            {
                Position = Target.WorldTranslation;
                Rotation = XRMath.QuaternionToEuler(Target.WorldRotation);
            }

            if (PositionOffset != Vector3.Zero)
                Transform.SetWorldTranslation(Transform.WorldTranslation + PositionOffset);
            
            if (PositionWeight > 0f)
                Transform.SetWorldTranslation(Vector3.Lerp(Transform.WorldTranslation, Position, PositionWeight));
            
            if (RotationOffset != Vector3.Zero)
                Transform.SetWorldRotation(Quaternion.CreateFromYawPitchRoll(RotationOffset.Y, RotationOffset.X, RotationOffset.Z) * Transform.WorldRotation);
            
            if (RotationWeight > 0f)
                Transform.SetWorldRotation(Quaternion.Slerp(Transform.WorldRotation, Quaternion.CreateFromYawPitchRoll(Rotation.Y, Rotation.X, Rotation.Z), RotationWeight));
        }
    }
}
