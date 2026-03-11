using Extensions;
using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class RingModulatorEffect : AudioEffect
    {
        public RingModulatorEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.RingModulator;
        }
        /// <summary>
        /// This is the frequency of the carrier signal. If the carrier signal is slowly
        /// varying (less than 20 Hz), the result is a slow amplitude variation effect (tremolo).
        /// Unit: Hz
        /// Range [0.0f .. 8000.0f]
        /// Default: 440.0f
        /// </summary>
        public float RingModulatorFrequency
        {
            get => GetEffectParameter(EffectFloat.RingModulatorFrequency);
            set => SetEffectParameter(EffectFloat.RingModulatorFrequency, value.Clamp(0.0f, 8000.0f));
        }
        /// <summary>
        /// This controls the cutoff frequency at which the input signal is high-pass filtered before being ring modulated. 
        /// Unit: Hz
        /// Range [0.0f .. 24000.0f]
        /// Default: 800.0f
        /// </summary>
        public float RingModulatorHighpassCutoff
        {
            get => GetEffectParameter(EffectFloat.RingModulatorHighpassCutoff);
            set => SetEffectParameter(EffectFloat.RingModulatorHighpassCutoff, value.Clamp(0.0f, 24000.0f));
        }
        public enum ERingModulatorWaveform
        {
            Sinusoid = 0,
            Sawtooth = 1,
            Square = 2
        }
        /// <summary>
        /// This controls which waveform is used as the carrier signal. Traditional ring
        /// modulator and tremolo effects generally use a sinusoidal carrier.
        /// Unit: (0) Sinusoid, (1) Sawtooth, (2) Square 
        /// Range [0 .. 2]
        /// Default: 0
        /// </summary>
        public ERingModulatorWaveform RingModulatorWaveform
        {
            get => (ERingModulatorWaveform)GetEffectParameter(EffectInteger.RingModulatorWaveform);
            set => SetEffectParameter(EffectInteger.RingModulatorWaveform, ((int)value).Clamp(0, 2));
        }
    }
}