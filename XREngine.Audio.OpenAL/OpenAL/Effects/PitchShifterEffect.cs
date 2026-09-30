using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class PitchShifterEffect : AudioEffect
    {
        public PitchShifterEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.PitchShifter;
        }
        /// <summary>
        /// This sets the number of semitones by which the pitch is shifted. There are 12
        /// semitones per octave.
        /// Unit: Semitones
        /// Range [-12 .. +12]
        /// Default: +12
        /// </summary>
        public int PitchShifterCoarseTune
        {
            get => GetEffectParameter(EffectInteger.PitchShifterCoarseTune);
            set => SetEffectParameter(EffectInteger.PitchShifterCoarseTune, value);
        }
        /// <summary>
        /// This sets the number of cents between Semitones a pitch is shifted. A Cent is
        /// 1/100th of a Semitone.
        /// Unit: Cents
        /// Range [-50 .. +50]
        /// Default: 0
        /// </summary>
        public int PitchShifterFineTune
        {
            get => GetEffectParameter(EffectInteger.PitchShifterFineTune);
            set => SetEffectParameter(EffectInteger.PitchShifterFineTune, value);
        }
    }
}