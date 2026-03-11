using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class EchoEffect : AudioEffect
    {
        public EchoEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.Echo;
        }
        /// <summary>
        /// This property controls the delay between the original sound and the first "tap",
        /// or echo instance. Subsequently, the value for Echo Delay is used to determine
        /// the time delay between each "second tap" and the next "first tap".
        /// Unit: Seconds
        /// Range [0.0f .. 0.207f]
        /// Default: 0.1f
        /// </summary>
        public float EchoDelay
        {
            get => GetEffectParameter(EffectFloat.EchoDelay);
            set => SetEffectParameter(EffectFloat.EchoDelay, value);
        }
        /// <summary>
        /// This property controls the delay between the "first tap" and the "second tap".
        /// Subsequently, the value for Echo LR Delay is used to determine the time delay
        /// between each "first tap" and the next "second tap".
        /// Unit: Seconds
        /// Range [0.0f .. 0.404f]
        /// Default: 0.1f
        /// </summary>
        public float EchoLRDelay
        {
            get => GetEffectParameter(EffectFloat.EchoLRDelay);
            set => SetEffectParameter(EffectFloat.EchoLRDelay, value);
        }
        /// <summary>
        /// This property controls the amount of high frequency damping applied to each echo.
        /// As the sound is subsequently fed back for further echoes, damping results in
        /// an echo which progressively gets softer in tone as well as intensity.
        /// Range [0.0f .. 0.99f]
        /// Default: 0.5f
        /// </summary>
        public float EchoDamping
        {
            get => GetEffectParameter(EffectFloat.EchoDamping);
            set => SetEffectParameter(EffectFloat.EchoDamping, value);
        }
        /// <summary>
        /// This property controls the amount of feedback the output signal fed back into
        /// the input. Use this parameter to create "cascading" echoes. At full magnitude,
        /// the identical sample will repeat endlessly. Below full magnitude, the sample
        /// will repeat and fade.
        /// Range [0.0f .. 1.0f]
        /// Default: 0.5f
        /// </summary>
        public float EchoFeedback
        {
            get => GetEffectParameter(EffectFloat.EchoFeedback);
            set => SetEffectParameter(EffectFloat.EchoFeedback, value);
        }
        /// <summary>
        /// This property controls how hard panned the individual echoes are. With a value
        /// of 1.0f, the first "tap" will be panned hard left, and the second "tap" hard
        /// right. –1.0f gives the opposite result and values near to 0.0f result in less
        /// emphasized panning.
        /// Range [-1.0f .. +1.0f]
        /// Default: -1.0f
        /// </summary>
        public float EchoSpread
        {
            get => GetEffectParameter(EffectFloat.EchoSpread);
            set => SetEffectParameter(EffectFloat.EchoSpread, value);
        }
    }
}