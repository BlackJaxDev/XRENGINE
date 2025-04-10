using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Rotates around the parent transform about the local Y axis.
    /// </summary>
    /// <param name="parent"></param>
    public class OrbitTransform : TransformBase
    {
        public OrbitTransform() { }
        public OrbitTransform(TransformBase? parent)
            : base(parent) { }

        private float _angle = 0.0f;
        private float _radius = 0.0f;
        private bool _ignoreRotation = false;

        /// <summary>
        /// The distance from the parent transform.
        /// If 0, the transform will be at the parent's origin.
        /// </summary>
        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        /// <summary>
        /// The angle in degrees to rotate around the Y axis.
        /// </summary>
        public float AngleDegrees
        {
            get => _angle;
            set => SetField(ref _angle, value);
        }

        /// <summary>
        /// If true, the transform will ignore the rotation of the orbit and will have no local rotation.
        /// If false, the transform will look at the parent transform.
        /// </summary>
        public bool IgnoreRotation 
        {
            get => _ignoreRotation;
            set => SetField(ref _ignoreRotation, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Radius):
                case nameof(AngleDegrees):
                    MarkLocalModified();
                    break;
            }
        }

        protected override Matrix4x4 CreateLocalMatrix()
        {
            var mtx = Matrix4x4.CreateTranslation(new Vector3(0, 0, Radius)) * Matrix4x4.CreateRotationY(float.DegreesToRadians(AngleDegrees));
            return IgnoreRotation ? Matrix4x4.CreateTranslation(mtx.Translation) : mtx;
        }
    }
}