using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class CompressorEffect : AudioEffect
    {
        public CompressorEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.Compressor;
        }
        /// <summary>
        /// Enabling this will result in audio exhibiting smaller variation in intensity
        /// between the loudest and quietest portions.
        /// Default: True
        /// </summary>
        public bool Enabled
        {
            get => GetEffectParameter(EffectInteger.CompressorOnoff) != 0;
            set => SetEffectParameter(EffectInteger.CompressorOnoff, value ? 1 : 0);
        }
    }
}