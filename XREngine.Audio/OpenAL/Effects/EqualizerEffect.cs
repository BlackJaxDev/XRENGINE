using Extensions;
using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class EqualizerEffect : AudioEffect
    {
        public EqualizerEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.Equalizer;
        }
        /// <summary>
        /// This property controls amount of cut or boost on the low frequency range.
        /// Range [0.126f .. 7.943f]
        /// Default: 1.0f
        /// </summary>
        public float EqualizerLowGain
        {
            get => GetEffectParameter(EffectFloat.EqualizerLowGain);
            set => SetEffectParameter(EffectFloat.EqualizerLowGain, value.Clamp(0.126f, 7.943f));
        }
        /// <summary>
        /// This property controls the low frequency below which signal will be cut off.
        /// Unit: Hz
        /// Range [50.0f .. 800.0f]
        /// Default: 200.0f
        /// </summary>
        public float EqualizerLowCutoff
        {
            get => GetEffectParameter(EffectFloat.EqualizerLowCutoff);
            set => SetEffectParameter(EffectFloat.EqualizerLowCutoff, value.Clamp(50.0f, 800.0f));
        }
        /// <summary>
        /// This property allows you to cut/boost signal on the "mid1" range. 
        /// Range [0.126f .. 7.943f]
        /// Default: 1.0f
        /// </summary>
        public float EqualizerMid1Gain
        {
            get => GetEffectParameter(EffectFloat.EqualizerMid1Gain);
            set => SetEffectParameter(EffectFloat.EqualizerMid1Gain, value.Clamp(0.126f, 7.943f));
        }
        /// <summary>
        /// This property sets the center frequency for the "mid1" range.
        /// Unit: Hz 
        /// Range [200.0f .. 3000.0f]
        /// Default: 500.0f
        /// </summary>
        public float EqualizerMid1Center
        {
            get => GetEffectParameter(EffectFloat.EqualizerMid1Center);
            set => SetEffectParameter(EffectFloat.EqualizerMid1Center, value.Clamp(200.0f, 3000.0f));
        }
        /// <summary>
        /// This property controls the width of the "mid1" range.
        /// Range [0.01f .. 1.0f]
        /// Default: 1.0f
        /// </summary>
        public float EqualizerMid1Width
        {
            get => GetEffectParameter(EffectFloat.EqualizerMid1Width);
            set => SetEffectParameter(EffectFloat.EqualizerMid1Width, value.Clamp(0.01f, 1.0f));
        }
        /// <summary>
        /// This property allows you to cut/boost signal on the "mid2" range.
        /// Range [0.126f .. 7.943f]
        /// Default: 1.0f
        /// </summary>
        public float EqualizerMid2Gain
        {
            get => GetEffectParameter(EffectFloat.EqualizerMid2Gain);
            set => SetEffectParameter(EffectFloat.EqualizerMid2Gain, value.Clamp(0.126f, 7.943f));
        }
        /// <summary>
        /// This property sets the center frequency for the "mid2" range.
        /// Unit: Hz
        /// Range [1000.0f .. 8000.0f]
        /// Default: 3000.0f
        /// </summary>
        public float EqualizerMid2Center
        {
            get => GetEffectParameter(EffectFloat.EqualizerMid2Center);
            set => SetEffectParameter(EffectFloat.EqualizerMid2Center, value.Clamp(1000.0f, 8000.0f));
        }
        /// <summary>
        /// This property controls the width of the "mid2" range.
        /// Range [0.01f .. 1.0f]
        /// Default: 1.0f
        /// </summary>
        public float EqualizerMid2Width
        {
            get => GetEffectParameter(EffectFloat.EqualizerMid2Width);
            set => SetEffectParameter(EffectFloat.EqualizerMid2Width, value.Clamp(0.01f, 1.0f));
        }
        /// <summary>
        /// This property allows to cut/boost the signal at high frequencies.
        /// Range [0.126f .. 7.943f]
        /// Default: 1.0f
        /// </summary>
        public float EqualizerHighGain
        {
            get => GetEffectParameter(EffectFloat.EqualizerHighGain);
            set => SetEffectParameter(EffectFloat.EqualizerHighGain, value.Clamp(0.126f, 7.943f));
        }
        /// <summary>
        /// This property controls the high frequency above which signal will be cut off.
        /// Unit: Hz
        /// Range [4000.0f .. 16000.0f]
        /// Default: 6000.0f
        /// </summary>
        public float EqualizerHighCutoff
        {
            get => GetEffectParameter(EffectFloat.EqualizerHighCutoff);
            set => SetEffectParameter(EffectFloat.EqualizerHighCutoff, value.Clamp(4000.0f, 16000.0f));
        }

    }
}