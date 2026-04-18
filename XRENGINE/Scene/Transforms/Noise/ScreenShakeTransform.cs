using XREngine.Extensions;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Transforms
{
    /// <summary>
    /// Rotates the scene node by a random amount in each axis, with a specified intensity and frequency.
    /// Noise intensity is controlled by the Trauma property and decreases over time.
    /// </summary>
    public class ScreenShakeTransform : NoiseRotationTransform
    {
        private float _trauma = 0.0f;
        private float _maxTrauma = 1.0f;
        private float _traumaDecrement = 0.3f;

        public ScreenShakeTransform(TransformBase? parent) : base(parent) { }
        public ScreenShakeTransform(
            TransformBase? parent,
            float traumaDecrementPerSec,
            float maxTrauma,
            float currentTrauma) : base(parent)
        {
            TraumaDecrementPerSecond = traumaDecrementPerSec;
            MaxTrauma = maxTrauma;
            Trauma = currentTrauma;
        }
        public ScreenShakeTransform(
            TransformBase? parent,
            float maxPitch,
            float maxYaw,
            float maxRoll,
            float noiseFreq,
            float traumaDecrementPerSec,
            float maxTrauma,
            float currentTrauma) 
            : base(parent, maxPitch, maxYaw, maxRoll, noiseFreq, 0.0f)
        {
            TraumaDecrementPerSecond = traumaDecrementPerSec;
            MaxTrauma = maxTrauma;
            Trauma = currentTrauma;
        }

        /// <summary>
        /// How much the amount of screenshake should decrease every second.
        /// </summary>
        public float TraumaDecrementPerSecond
        {
            get => _traumaDecrement;
            set => SetField(ref _traumaDecrement, value);
        }
        /// <summary>
        /// The maximum amount of screenshake allowed.
        /// </summary>
        public float MaxTrauma
        {
            get => _maxTrauma;
            set => SetField(ref _maxTrauma, value);
        }
        /// <summary>
        /// Add to this value to increase the amount of screenshake.
        /// </summary>
        public float Trauma
        {
            get => _trauma;
            set
            {
                if (SetField(ref _trauma, value.Clamp(0.0f, MaxTrauma)))
                {
                    float normalized = _trauma / MaxTrauma;
                    ShakeIntensity = normalized * normalized;
                }
            }
        }
        protected override void NoiseTick()
        {
            Trauma -= TraumaDecrementPerSecond * Engine.UndilatedDelta;
            base.NoiseTick();
        }
    }
}
