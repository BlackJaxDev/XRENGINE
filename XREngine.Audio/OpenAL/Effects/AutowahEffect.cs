using Extensions;
using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class AutowahEffect : AudioEffect
    {
        public AutowahEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.Autowah;
        }
        /// <summary>
        /// This property controls the time the filtering effect takes to sweep from minimum
        /// to maximum center frequency when it is triggered by input signal. 
        /// Unit: Seconds
        /// Range [0.0001f .. 1.0f] Default: 0.06f
        /// </summary>
        public float AutowahAttackTime
        {
            get => GetEffectParameter(EffectFloat.AutowahAttackTime);
            set => SetEffectParameter(EffectFloat.AutowahAttackTime, value.Clamp(0.0001f, 1.0f));
        }
        /// <summary>
        /// This property controls the time the filtering effect takes to sweep from maximum
        /// back to base center frequency, when the input signal ends. 
        /// Unit: Seconds
        /// Range [0.0001f .. 1.0f]
        /// Default: 0.06f
        /// </summary>
        public float AutowahReleaseTime
        {
            get => GetEffectParameter(EffectFloat.AutowahReleaseTime);
            set => SetEffectParameter(EffectFloat.AutowahReleaseTime, value.Clamp(0.0001f, 1.0f));
        }
        /// <summary>
        /// This property controls the resonant peak, sometimes known as emphasis or Q, of
        /// the auto-wah band-pass filter.
        /// Range [2.0f .. 1000.0f]
        /// Default: 1000.0f
        /// </summary>
        public float AutowahResonance
        {
            get => GetEffectParameter(EffectFloat.AutowahResonance);
            set => SetEffectParameter(EffectFloat.AutowahResonance, value.Clamp(2.0f, 1000.0f));
        }
        /// <summary>
        /// This property controls the input signal level at which the band-pass filter will be fully opened.
        /// Range [0.00003f .. 31621.0f]
        /// Default: 11.22f
        /// </summary>
        public float AutowahPeakGain
        {
            get => GetEffectParameter(EffectFloat.AutowahPeakGain);
            set => SetEffectParameter(EffectFloat.AutowahPeakGain, value.Clamp(0.00003f, 31621.0f));
        }
    }
}