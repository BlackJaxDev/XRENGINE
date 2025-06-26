using Extensions;
using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Looks at another transform.
    /// </summary>
    /// <param name="parent"></param>
    public class LookatTransform : TransformBase
    {
        public LookatTransform() { }
        public LookatTransform(TransformBase? parent)
            : base(parent) { }

        private TransformBase? _target;
        private Vector3 _up = Globals.Up;
        private float _roll = 0.0f;

        /// <summary>
        /// The target transform to look at.
        /// </summary>
        public TransformBase? Target
        {
            get => _target;
            set => SetField(ref _target, value);
        }

        /// <summary>
        /// The world up vector to use when looking at the target.
        /// </summary>
        public Vector3 Up
        {
            get => _up;
            set => SetField(ref _up, value);
        }

        /// <summary>
        /// Rolls the transform after looking at the target.
        /// </summary>
        public float Roll
        {
            get => _roll;
            set => SetField(ref _roll, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Target):
                        if (Target is not null)
                            Target.WorldMatrixChanged -= OnTargetMatrixChanged;
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Up):
                    MarkWorldModified();
                    break;
                case nameof(Target):
                    if (Target is not null)
                        Target.WorldMatrixChanged += OnTargetMatrixChanged;
                    MarkWorldModified();
                    break;
            }
        }

        private void OnTargetMatrixChanged(TransformBase @base, Matrix4x4 worldMatrix)
            => MarkWorldModified();

        protected override Matrix4x4 CreateWorldMatrix()
        {
            Vector3 parentPos = Parent?.WorldMatrix.Translation ?? Vector3.Zero;
            Vector3 targetPos = Target?.WorldMatrix.Translation ?? Vector3.Zero;
            Matrix4x4 lookat = Matrix4x4.CreateLookAt(parentPos, targetPos, Globals.Up);

            if (Roll.IsZero())
                return lookat;

            return lookat * Matrix4x4.CreateRotationZ(Roll);
        }

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.Identity;
    }
}