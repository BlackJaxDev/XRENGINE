using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class DistortionEffect : AudioEffect
    {
        public DistortionEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.Distortion;
        }
        /// <summary>
        /// This property controls the shape of the distortion. The higher the value for
        /// Edge, the "dirtier" and "fuzzier" the effect.
        /// Range [0.0f .. 1.0f]
        /// Default: 0.2f
        /// </summary>
        public float DistortionEdge
        {
            get => GetEffectParameter(EffectFloat.DistortionEdge);
            set => SetEffectParameter(EffectFloat.DistortionEdge, value);
        }
        /// <summary>
        /// This property allows you to attenuate the distorted sound.
        /// Range [0.01f .. 1.0f]
        /// Default: 0.05f
        /// </summary>
        public float DistortionGain
        {
            get => GetEffectParameter(EffectFloat.DistortionGain);
            set => SetEffectParameter(EffectFloat.DistortionGain, value);
        }
        /// <summary>
        /// Input signals can have a low pass filter applied, to limit the amount of high
        /// frequency signal feeding into the distortion effect.
        /// Unit: Hz
        /// Range [80.0f .. 24000.0f]
        /// Default: 8000.0f
        /// </summary>
        public float DistortionLowpassCutoff
        {
            get => GetEffectParameter(EffectFloat.DistortionLowpassCutoff);
            set => SetEffectParameter(EffectFloat.DistortionLowpassCutoff, value);
        }
        /// <summary>
        /// This property controls the frequency at which the post-distortion attenuation (Distortion Gain) is active.
        /// Unit: Hz
        /// Range [80.0f .. 24000.0f]
        /// Default: 3600.0f
        /// </summary>
        public float DistortionEQCenter
        {
            get => GetEffectParameter(EffectFloat.DistortionEQCenter);
            set => SetEffectParameter(EffectFloat.DistortionEQCenter, value);
        }
        /// <summary>
        /// This property controls the bandwidth of the post-distortion attenuation.
        /// Unit: Hz
        /// Range [80.0f .. 24000.0f]
        /// Default: 3600.0f
        /// </summary>
        public float DistortionEQBandwidth
        {
            get => GetEffectParameter(EffectFloat.DistortionEQBandwidth);
            set => SetEffectParameter(EffectFloat.DistortionEQBandwidth, value);
        }
    }
}