using Silk.NET.OpenAL.Extensions.Creative;

namespace XREngine.Audio.Effects
{
    public class ReverbEffect : AudioEffect
    {
        public ReverbEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.Reverb;
        }
        /// <summary>
        /// Reverb Modal Density controls the coloration of the late reverb. Lowering the
        /// value adds more coloration to the late reverb.
        /// Range [0.0f .. 1.0f]
        /// Default: 1.0f
        /// </summary>
        public float Density
        {
            get => GetEffectParameter(EffectFloat.ReverbDensity);
            set => SetEffectParameter(EffectFloat.ReverbDensity, value);
        }
        /// <summary>
        /// The Reverb Diffusion property controls the echo density in the reverberation
        /// decay. The default 1.0f provides the highest density. Reducing diffusion gives
        /// the reverberation a more "grainy" character that is especially noticeable with
        /// percussive sound sources. If you set a diffusion value of 0.0f, the later reverberation
        /// sounds like a succession of distinct echoes.
        /// Range [0.0f .. 1.0f]
        /// Default: 1.0f
        /// </summary>
        public float Diffusion
        {
            get => GetEffectParameter(EffectFloat.ReverbDiffusion);
            set => SetEffectParameter(EffectFloat.ReverbDiffusion, value);
        }
        /// <summary>
        /// The Reverb Gain property is the master volume control for the reflected sound
        /// - both early reflections and reverberation - that the reverb effect adds to all
        /// sound sources. Ranges from 1.0 (0db) (the maximum amount) to 0.0 (-100db) (no
        /// reflected sound at all) are accepted.
        /// Units: Linear gain
        /// Range [0.0f .. 1.0f]
        /// Default: 0.32f
        /// </summary>
        public float Gain
        {
            get => GetEffectParameter(EffectFloat.ReverbGain);
            set => SetEffectParameter(EffectFloat.ReverbGain, value);
        }
        /// <summary>
        /// The Reverb Gain HF property further tweaks reflected sound by attenuating it
        /// at high frequencies. It controls a low-pass filter that applies globally to the
        /// reflected sound of all sound sources feeding the particular instance of the reverb
        /// effect. Ranges from 1.0f (0db) (no filter) to 0.0f (-100db) (virtually no reflected
        /// sound) are accepted.
        /// Units: Linear gain
        /// Range [0.0f .. 1.0f]
        /// Default: 0.89f
        /// </summary>
        public float GainHighFreq
        {
            get => GetEffectParameter(EffectFloat.ReverbGainHF);
            set => SetEffectParameter(EffectFloat.ReverbGainHF, value);
        }
        /// <summary>
        /// The Decay Time property sets the reverberation decay time. It ranges from 0.1f
        /// (typically a small room with very dead surfaces) to 20.0 (typically a large room
        /// with very live surfaces).
        /// Unit: Seconds
        /// Range [0.1f .. 20.0f]
        /// Default: 1.49f
        /// </summary>
        public float DecayTime
        {
            get => GetEffectParameter(EffectFloat.ReverbDecayTime);
            set => SetEffectParameter(EffectFloat.ReverbDecayTime, value);
        }
        /// <summary>
        /// The Decay HF Ratio property sets the spectral quality of the Decay Time parameter.
        /// It is the ratio of high-frequency decay time relative to the time set by Decay Time.. 
        /// Unit: linear multiplier
        /// Range [0.1f .. 2.0f]
        /// Default: 0.83f
        /// </summary>
        public float DecayHighFreqRatio
        {
            get => GetEffectParameter(EffectFloat.ReverbDecayHFRatio);
            set => SetEffectParameter(EffectFloat.ReverbDecayHFRatio, value);
        }
        /// <summary>
        /// The Reflections Gain property controls the overall amount of initial reflections
        /// relative to the Gain property. The value of Reflections Gain ranges from a maximum
        /// of 3.16f (+10 dB) to a minimum of 0.0f (-100 dB) (no initial reflections at all),
        /// and is corrected by the value of the Gain property.
        /// Unit: Linear gain
        /// Range [0.0f .. 3.16f]
        /// Default: 0.05f
        /// </summary>
        public float ReflectionsGain
        {
            get => GetEffectParameter(EffectFloat.ReverbReflectionsGain);
            set => SetEffectParameter(EffectFloat.ReverbReflectionsGain, value);
        }
        /// <summary>
        /// The Reflections Delay property is the amount of delay between the arrival time
        /// of the direct path from the source to the first reflection from the source. It
        /// ranges from 0 to 300 milliseconds.
        /// Unit: Seconds
        /// Range [0.0f .. 0.3f]
        /// Default: 0.007f
        /// </summary>
        public float ReflectionsDelay
        {
            get => GetEffectParameter(EffectFloat.ReverbReflectionsDelay);
            set => SetEffectParameter(EffectFloat.ReverbReflectionsDelay, value);
        }
        /// <summary>
        /// The Late Reverb Gain property controls the overall amount of later reverberation
        /// relative to the Gain property. The value of Late Reverb Gain ranges from a maximum
        /// of 10.0f (+20 dB) to a minimum of 0.0f (-100 dB) (no late reverberation at all).
        /// Unit: Linear gain
        /// Range [0.0f .. 10.0f]
        /// Default: 1.26f
        /// </summary>
        public float LateReverbGain
        {
            get => GetEffectParameter(EffectFloat.ReverbLateReverbGain);
            set => SetEffectParameter(EffectFloat.ReverbLateReverbGain, value);
        }
        /// <summary>
        /// The Late Reverb Delay property defines the begin time of the late reverberation
        /// relative to the time of the initial reflection (the first of the early reflections).
        /// It ranges from 0 to 100 milliseconds.
        /// Unit: Seconds
        /// Range [0.0f .. 0.1f]
        /// Default: 0.011f
        /// </summary>
        public float LateReverbDelay
        {
            get => GetEffectParameter(EffectFloat.ReverbLateReverbDelay);
            set => SetEffectParameter(EffectFloat.ReverbLateReverbDelay, value);
        }
        /// <summary>
        /// The Air Absorption Gain HF property controls the distance-dependent attenuation
        /// at high frequencies caused by the propagation medium and applies to reflected
        /// sound only.
        /// Unit: Linear gain per meter
        /// Range [0.892f .. 1.0f]
        /// Default: 0.994f
        /// </summary>
        public float AirAbsorptionGainHighFreq
        {
            get => GetEffectParameter(EffectFloat.ReverbAirAbsorptionGainHF);
            set => SetEffectParameter(EffectFloat.ReverbAirAbsorptionGainHF, value);
        }
        /// <summary>
        /// The Room Rolloff Factor property is one of two methods available to attenuate
        /// the reflected sound (containing both reflections and reverberation) according
        /// to source-listener distance. It's defined the same way as OpenAL's Rolloff Factor,
        /// but operates on reverb sound instead of direct-path sound.
        /// Unit: Linear multiplier
        /// Range [0.0f .. 10.0f]
        /// Default: 0.0f
        /// </summary>
        public float RoomRolloffFactor
        {
            get => GetEffectParameter(EffectFloat.ReverbRoomRolloffFactor);
            set => SetEffectParameter(EffectFloat.ReverbRoomRolloffFactor, value);
        }
        /// <summary>
        /// When this flag is set, the high-frequency decay time automatically stays below
        /// a limit value that's derived from the setting of the property Air Absorption HF. 
        /// Default: True
        /// </summary>
        public bool DecayHighFreqLimit
        {
            get => GetEffectParameter(EffectInteger.ReverbDecayHFLimit) != 0;
            set => SetEffectParameter(EffectInteger.ReverbDecayHFLimit, value ? 1 : 0);
        }
    }
}