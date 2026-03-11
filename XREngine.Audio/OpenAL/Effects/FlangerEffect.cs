using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class FlangerEffect : AudioEffect
    {
        public FlangerEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.Flanger;
        }
        public enum EFlangerWaveform
        {
            Sinusoid = 0,
            Triangle = 1
        }
        /// <summary>
        /// Selects the shape of the low-frequency oscillator waveform that controls the
        /// amount of the delay of the sampled signal.
        /// Default: Triangle
        /// </summary>
        public EFlangerWaveform FlangerWaveform
        {
            get => (EFlangerWaveform)GetEffectParameter(EffectInteger.FlangerWaveform);
            set => SetEffectParameter(EffectInteger.FlangerWaveform, (int)value);
        }
        /// <summary>
        /// This changes the phase difference between the left and right low-frequency oscillator's.
        /// At zero degrees the two low-frequency oscillators are synchronized. 
        /// Range [-180 .. +180] 
        /// Default: 0
        /// </summary>
        public int FlangerPhase
        {
            get => GetEffectParameter(EffectInteger.FlangerPhase);
            set => SetEffectParameter(EffectInteger.FlangerPhase, value);
        }
        /// <summary>
        /// The number of times per second the low-frequency oscillator controlling the amount
        /// of delay repeats. 
        /// Range [0.0f .. 10.0f]
        /// Default: 0.27f
        /// </summary>
        public float FlangerRate
        {
            get => GetEffectParameter(EffectFloat.FlangerRate);
            set => SetEffectParameter(EffectFloat.FlangerRate, value);
        }
        /// <summary>
        /// The ratio by which the delay time is modulated by the low-frequency oscillator. 
        /// Range [0.0f .. 1.0f]
        /// Default: 1.0f
        /// </summary>
        public float FlangerDepth
        {
            get => GetEffectParameter(EffectFloat.FlangerDepth);
            set => SetEffectParameter(EffectFloat.FlangerDepth, value);
        }
        /// <summary>
        /// This is the amount of the output signal level fed back into the effect's input.
        /// A negative value will reverse the phase of the feedback signal.
        /// Range [-1.0f .. +1.0f]
        /// Default: -0.5f
        /// </summary>
        public float FlangerFeedback
        {
            get => GetEffectParameter(EffectFloat.FlangerFeedback);
            set => SetEffectParameter(EffectFloat.FlangerFeedback, value);
        }
        /// <summary>
        /// The average amount of time the sample is delayed before it is played back. 
        /// When used with the Feedback property it's the amount of time between iterations of the sample. 
        /// Unit: Seconds 
        /// Range [0.0f .. 0.004f] 
        /// Default: 0.002f
        /// </summary>
        public float FlangerDelay
        {
            get => GetEffectParameter(EffectFloat.FlangerDelay);
            set => SetEffectParameter(EffectFloat.FlangerDelay, value);
        }
    }
}