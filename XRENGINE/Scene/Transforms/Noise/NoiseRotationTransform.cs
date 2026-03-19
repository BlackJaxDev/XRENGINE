using DotnetNoise;
using System.Numerics;
using XREngine.Data.Transforms.Rotations;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Transforms
{
    /// <summary>
    /// Rotates the scene node by a random amount in each axis, with a specified intensity and frequency.
    /// </summary>
    public class NoiseRotationTransform : TransformBase
    {
        public NoiseRotationTransform(TransformBase? parent) : base(parent) { }
        public NoiseRotationTransform(TransformBase? parent, float maxPitch, float maxYaw, float maxRoll, float noiseFreq, float currentShakeIntensity) : base(parent)
        {
            MaxPitch = maxPitch;
            MaxYaw = maxYaw;
            MaxRoll = maxRoll;
            NoiseFrequency = noiseFreq;
            ShakeIntensity = currentShakeIntensity;
        }

        public float ShakeIntensity
        {
            get => _shakeIntensity;
            set => SetField(ref _shakeIntensity, value);
        }
        public float MaxYaw
        {
            get => _maxYaw;
            set => SetField(ref _maxYaw, value);
        }
        public float MaxPitch
        {
            get => _maxPitch;
            set => SetField(ref _maxPitch, value);
        }
        public float MaxRoll
        {
            get => _maxRoll;
            set => SetField(ref _maxRoll, value);
        }
        public float NoiseFrequency
        {
            get => _noiseFrequency;
            set
            {
                SetField(ref _noiseFrequency, value);
                _noise.Frequency = _noiseFrequency;
            }
        }

        private float _time = 0.0f;
        private float _noiseFrequency = 500.0f;
        private float _shakeIntensity = 0.0f;
        private float _maxYaw = 30.0f;
        private float _maxPitch = 30.0f;
        private float _maxRoll = 20.0f;
        private readonly Rotator _rotation = new();
        private readonly FastNoise _noise = new();

        protected override void OnSceneNodeActivated()
        {
            base.OnSceneNodeActivated();
            _time = 0.0f;
            _noise.Frequency = _noiseFrequency;
            RegisterTick(ETickGroup.Normal, (int)ETickOrder.Logic, NoiseTick);
        }

        protected override void OnSceneNodeDeactivated()
        {
            base.OnSceneNodeDeactivated();
            UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Logic, NoiseTick);
            _time = 0.0f;
        }

        protected virtual void NoiseTick()
        {
            _time += Engine.UndilatedDelta;
            _rotation.SetRotations(
                MaxPitch * ShakeIntensity * _noise.GetPerlin(21.0f, _time),
                MaxYaw * ShakeIntensity * _noise.GetPerlin(20.0f, _time),
                MaxRoll * ShakeIntensity * _noise.GetPerlin(22.0f, _time));
            MarkLocalModified();
        }

        public override Vector3 LocalTranslation
            => Vector3.Zero;

        public override Quaternion LocalRotation
            => Quaternion.Normalize(_rotation.ToQuaternion());

        public override Quaternion InverseLocalRotation
            => Quaternion.Normalize(Quaternion.Inverse(LocalRotation));

        public override Quaternion WorldRotation
            => Parent is null
                ? LocalRotation
                : Quaternion.Normalize(ParentWorldRotation * LocalRotation);

        public override Quaternion InverseWorldRotation
            => Parent is null
                ? InverseLocalRotation
                : Quaternion.Normalize(InverseLocalRotation * ParentInverseWorldRotation);

        public override Quaternion RenderRotation
            => Parent is null
                ? LocalRotation
                : Quaternion.Normalize(Parent.RenderRotation * LocalRotation);

        public override Quaternion InverseRenderRotation
            => Parent is null
                ? InverseLocalRotation
                : Quaternion.Normalize(InverseLocalRotation * Parent.InverseRenderRotation);

        protected override Matrix4x4 CreateLocalMatrix()
            => _rotation.GetMatrix();
    }
}
