using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    public class UIRotationTransform(TransformBase? parent) : UITransform(parent)
    {
        public UIRotationTransform() : this(null) { }

        private float _degreeRotation = 0.0f;
        /// <summary>
        /// The rotation angle of the component in degrees.
        /// </summary>
        public float DegreeRotation
        {
            get => _degreeRotation;
            set => SetField(ref _degreeRotation, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(DegreeRotation):
                    MarkLocalModified();
                    break;
            }
        }

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.CreateRotationZ(XRMath.DegToRad(DegreeRotation)) * base.CreateLocalMatrix();
    }
}
