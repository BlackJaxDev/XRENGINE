using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class FrequencyShifterEffect : AudioEffect
    {
        public FrequencyShifterEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.FrequencyShifter;
        }
        /// <summary>
        /// This is the carrier frequency. For carrier frequencies below the audible range,
        /// the single sideband modulator may produce phaser effects, spatial effects or
        /// a slight pitch-shift. As the carrier frequency increases, the timbre of the sound
        /// is affected.
        /// Unit: Hz
        /// Range [0.0f .. 24000.0f]
        /// Default: 0.0f
        /// </summary>
        public float FrequencyShifterFrequency
        {
            get => GetEffectParameter(EffectFloat.FrequencyShifterFrequency);
            set => SetEffectParameter(EffectFloat.FrequencyShifterFrequency, value);
        }
        public enum EDirection : int
        {
            Down = 0,
            Up = 1,
            Off = 2
        }
        /// <summary>
        /// These select which internal signals are added together to produce the output.
        /// Default: Down
        /// </summary>
        public EDirection FrequencyShifterLeftDirection
        {
            get => (EDirection)GetEffectParameter(EffectInteger.FrequencyShifterLeftDirection);
            set => SetEffectParameter(EffectInteger.FrequencyShifterLeftDirection, (int)value);
        }
        /// <summary>
        /// These select which internal signals are added together to produce the output.
        /// Default: Down
        /// </summary>
        public EDirection FrequencyShifterRightDirection
        {
            get => (EDirection)GetEffectParameter(EffectInteger.FrequencyShifterRightDirection);
            set => SetEffectParameter(EffectInteger.FrequencyShifterRightDirection, (int)value);
        }
    }
}