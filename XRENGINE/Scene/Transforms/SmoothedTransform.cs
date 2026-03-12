using System.Numerics;
using XREngine.Components;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Can be used to smooth out tranformations without getting in the way of components expecting a regular Transform.
    /// </summary>
    public class SmoothedTransform : Transform
    {
        private Quaternion _currentRotation = Quaternion.Identity;
        private Vector3 _currentTranslation = Vector3.Zero;
        private Vector3 _currentScale = Vector3.One;
        private float _rotationSmoothingSpeed = 1.0f;
        private float _translationSoothingSpeed = 1.0f;
        private float _scaleSmoothingSpeed = 1.0f;

        public float RotationSmoothingSpeed
        {
            get => _rotationSmoothingSpeed;
            set => SetField(ref _rotationSmoothingSpeed, value);
        }
        public float TranslationSmoothingSpeed
        {
            get => _translationSoothingSpeed;
            set => SetField(ref _translationSoothingSpeed, value);
        }
        public float ScaleSmoothingSpeed
        {
            get => _scaleSmoothingSpeed;
            set => SetField(ref _scaleSmoothingSpeed, value);
        }
        public Quaternion CurrentRotation => _currentRotation;
        public Vector3 CurrentTranslation => _currentTranslation;
        public Vector3 CurrentScale => _currentScale;

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(World):
                        UnregisterTick(ETickGroup.Normal, ETickOrder.Logic, Lerp);
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
                case nameof(World):
                    RegisterTick(ETickGroup.Normal, ETickOrder.Logic, Lerp);
                    break;
            }
        }

        protected override Matrix4x4 TRS() =>
            Matrix4x4.CreateScale(_currentScale) *
            Matrix4x4.CreateFromQuaternion(_currentRotation) *
            Matrix4x4.CreateTranslation(_currentTranslation);
        protected override Matrix4x4 STR() =>
            Matrix4x4.CreateFromQuaternion(_currentRotation) *
            Matrix4x4.CreateTranslation(_currentTranslation) *
            Matrix4x4.CreateScale(_currentScale);
        protected override Matrix4x4 RST() =>
            Matrix4x4.CreateTranslation(_currentTranslation) *
            Matrix4x4.CreateScale(_currentScale) *
            Matrix4x4.CreateFromQuaternion(_currentRotation);
        protected override Matrix4x4 SRT() =>
            Matrix4x4.CreateTranslation(_currentTranslation) *
            Matrix4x4.CreateFromQuaternion(_currentRotation) *
            Matrix4x4.CreateScale(_currentScale);
        protected override Matrix4x4 TSR() =>
            Matrix4x4.CreateFromQuaternion(_currentRotation) *
            Matrix4x4.CreateScale(_currentScale) *
            Matrix4x4.CreateTranslation(_currentTranslation);
        protected override Matrix4x4 RTS() =>
            Matrix4x4.CreateScale(_currentScale) *
            Matrix4x4.CreateTranslation(_currentTranslation) *
            Matrix4x4.CreateFromQuaternion(_currentRotation);

        private void Lerp()
        {
            _currentRotation = Quaternion.Slerp(_currentRotation, Rotation, RotationSmoothingSpeed * Engine.Delta);
            _currentTranslation = Vector3.Lerp(_currentTranslation, Translation, TranslationSmoothingSpeed * Engine.Delta);
            _currentScale = Vector3.Lerp(_currentScale, Scale, ScaleSmoothingSpeed * Engine.Delta);
            MarkLocalModified();
        }
    }
}
