using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class VocalMorpherEffect : AudioEffect
    {
        public VocalMorpherEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.VocalMorpher;
        }
        /// <summary>
        /// Sets the vocal morpher 4-band formant filter A, used to impose vocal tract effects
        /// upon the input signal. The vocal morpher is not necessarily intended for use
        /// on voice signals; it is primarily intended for pitched noise effects, vocal-like
        /// wind effects, etc. 
        /// Range [0 .. 29]
        /// Default: 0, "Phoneme A"
        /// </summary>
        public FormantFilterSettings VocalMorpherPhonemeA
        {
            get => (FormantFilterSettings)GetEffectParameter(EffectInteger.VocalMorpherPhonemeA);
            set => SetEffectParameter(EffectInteger.VocalMorpherPhonemeA, (int)value);
        }
        /// <summary>
        /// Sets the vocal morpher 4-band formant filter B, used to impose vocal tract effects
        /// upon the input signal. The vocal morpher is not necessarily intended for use
        /// on voice signals; it is primarily intended for pitched noise effects, vocal-like
        /// wind effects, etc.
        /// Range [0 .. 29]
        /// Default: 10, "Phoneme ER"
        /// </summary>
        public FormantFilterSettings VocalMorpherPhonemeB
        {
            get => (FormantFilterSettings)GetEffectParameter(EffectInteger.VocalMorpherPhonemeB);
            set => SetEffectParameter(EffectInteger.VocalMorpherPhonemeB, (int)value);
        }
        /// <summary>
        /// This is used to adjust the pitch of phoneme filter A in 1-semitone increments.
        /// Unit: Semitones
        /// Range [-24 .. +24]
        /// Default: 0
        /// </summary>
        public int VocalMorpherPhonemeACoarseTuning
        {
            get => GetEffectParameter(EffectInteger.VocalMorpherPhonemeACoarseTuning);
            set => SetEffectParameter(EffectInteger.VocalMorpherPhonemeACoarseTuning, value);
        }
        /// <summary>
        /// This is used to adjust the pitch of phoneme filter B in 1-semitone increments.
        /// Unit: Semitones
        /// Range [-24 .. +24]
        /// Default: 0
        /// </summary>
        public int VocalMorpherPhonemeBCoarseTuning
        {
            get => GetEffectParameter(EffectInteger.VocalMorpherPhonemeBCoarseTuning);
            set => SetEffectParameter(EffectInteger.VocalMorpherPhonemeBCoarseTuning, value);
        }
        public enum EVocalMorpherWaveform
        {
            Sinusoid = 0,
            Triangle = 1,
            Sawtooth = 2
        }
        /// <summary>
        /// This controls the shape of the low-frequency oscillator used to morph between
        /// the two phoneme filters.
        /// Default: Sinusoid
        /// </summary>
        public EVocalMorpherWaveform VocalMorpherWaveform
        {
            get => (EVocalMorpherWaveform)GetEffectParameter(EffectInteger.VocalMorpherWaveform);
            set => SetEffectParameter(EffectInteger.VocalMorpherWaveform, (int)value);
        }
        /// <summary>
        /// This controls the frequency of the low-frequency oscillator used to morph between
        /// the two phoneme filters.
        /// Unit: Hz
        /// Range [0.0f .. 10.0f]
        /// Default: 1.41f
        /// </summary>
        public float VocalMorpherRate
        {
            get => GetEffectParameter(EffectFloat.VocalMorpherRate);
            set => SetEffectParameter(EffectFloat.VocalMorpherRate, value);
        }
    }
}