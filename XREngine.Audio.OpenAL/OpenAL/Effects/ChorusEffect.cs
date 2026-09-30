using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class ChorusEffect : AudioEffect
    {
        public ChorusEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.Chorus;
        }
        public enum EChorusWaveform
        {
            Sinusoid = 0,
            Triangle = 1
        }
        /// <summary>
        /// This property sets the waveform shape of the low-frequency oscillator that controls
        /// the delay time of the delayed signals.
        /// Default: Triangle
        /// </summary>
        public EChorusWaveform ChorusWaveform
        {
            get => (EChorusWaveform)GetEffectParameter(EffectInteger.ChorusWaveform);
            set => SetEffectParameter(EffectInteger.ChorusWaveform, (int)value);
        }
        /// <summary>
        /// This property controls the phase difference between the left and right low-frequency
        /// oscillators. At zero degrees the two low-frequency oscillators are synchronized.
        /// Unit: Degrees
        /// Range [-180 .. 180]
        /// Default: 90
        /// </summary>
        public int ChorusPhase
        {
            get => GetEffectParameter(EffectInteger.ChorusPhase);
            set => SetEffectParameter(EffectInteger.ChorusPhase, value);
        }
        /// <summary>
        /// This property sets the modulation rate of the low-frequency oscillator that controls
        /// the delay time of the delayed signals.
        /// Unit: Hz
        /// Range [0.0f .. 10.0f]
        /// Default: 1.1f
        /// </summary>
        public float ChorusRate
        {
            get => GetEffectParameter(EffectFloat.ChorusRate);
            set => SetEffectParameter(EffectFloat.ChorusRate, value);
        }
        /// <summary>
        /// This property controls the amount by which the delay time is modulated by the
        /// low-frequency oscillator.
        /// Range [0.0f .. 1.0f]
        /// Default: 0.1f
        /// </summary>
        public float ChorusDepth
        {
            get => GetEffectParameter(EffectFloat.ChorusDepth);
            set => SetEffectParameter(EffectFloat.ChorusDepth, value);
        }
        /// <summary>
        /// This property controls the amount of processed signal that is fed back to the
        /// input of the chorus effect. Negative values will reverse the phase of the feedback
        /// signal. At full magnitude the identical sample will repeat endlessly.
        /// Range [-1.0f .. +1.0f]
        /// Default: +0.25f
        /// </summary>
        public float ChorusFeedback
        {
            get => GetEffectParameter(EffectFloat.ChorusFeedback);
            set => SetEffectParameter(EffectFloat.ChorusFeedback, value);
        }
        /// <summary>
        /// This property controls the average amount of time the sample is delayed before
        /// it is played back, and with feedback, the amount of time between iterations of
        /// the sample. Larger values lower the pitch.
        /// Unit: Seconds
        /// Range [0.0f .. 0.016f]
        /// Default: 0.016f
        /// </summary>
        public float ChorusDelay
        {
            get => GetEffectParameter(EffectFloat.ChorusDelay);
            set => SetEffectParameter(EffectFloat.ChorusDelay, value);
        }
    }
}