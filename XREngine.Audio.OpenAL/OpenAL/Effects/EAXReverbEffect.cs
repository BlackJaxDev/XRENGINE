using Silk.NET.OpenAL.Extensions.Creative;
using System.Numerics;

namespace XREngine.Audio.Effects
{
    public class EAXReverbEffect : AudioEffect
    {
        public EAXReverbEffect(EffectContext parentContext) : base(parentContext)
        {
            EffectType = EffectType.EaxReverb;
        }

        /// <summary>
        /// Reverb Modal Density controls the coloration of the late reverb.
        /// Range [0.0f .. 1.0f]
        /// Default: 1.0f
        /// </summary>
        public float Density
        {
            get => GetEffectParameter(EffectFloat.EaxReverbDensity);
            set => SetEffectParameter(EffectFloat.EaxReverbDensity, value);
        }
        /// <summary>
        /// The Reverb Diffusion property controls the echo density in the reverberation decay.
        /// Range [0.0f .. 1.0f]
        /// Default: 1.0f
        /// </summary>
        public float Diffusion
        {
            get => GetEffectParameter(EffectFloat.EaxReverbDiffusion);
            set => SetEffectParameter(EffectFloat.EaxReverbDiffusion, value);
        }
        /// <summary>
        /// Reverb Gain controls the level of the reverberant sound in an environment. A
        /// high level of reverb is characteristic of rooms with highly reflective walls
        /// and/or small dimensions. Unit: Linear gain Range [0.0f .. 1.0f] Default: 0.32f
        /// </summary>
        public float Gain
        {
            get => GetEffectParameter(EffectFloat.EaxReverbGain);
            set => SetEffectParameter(EffectFloat.EaxReverbGain, value);
        }
        /// <summary>
        /// Gain HF is used to attenuate the high frequency content of all the reflected
        /// sound in an environment. You can use this property to give a room specific spectral
        /// characteristic.
        /// Unit: Linear gain
        /// Range [0.0f .. 1.0f]
        /// Default: 0.89f
        /// </summary>
        public float GainHighFreq
        {
            get => GetEffectParameter(EffectFloat.EaxReverbGainHF);
            set => SetEffectParameter(EffectFloat.EaxReverbGainHF, value);
        }
        /// <summary>
        /// Gain LF is the low frequency counterpart to Gain HF. Use this to reduce or boost
        /// the low frequency content in an environment.
        /// Unit: Linear gain
        /// Range [0.0f .. 1.0f]
        /// Default: 1.0f
        /// </summary>
        public float GainLowFreq
        {
            get => GetEffectParameter(EffectFloat.EaxReverbGainLF);
            set => SetEffectParameter(EffectFloat.EaxReverbGainLF, value);
        }
        /// <summary>
        /// The Decay Time property sets the reverberation decay time. It ranges from 0.1f
        /// (typically a small room with very dead surfaces) to 20.0f (typically a large
        /// room with very live surfaces).
        /// Unit: Seconds
        /// Range [0.1f .. 20.0f]
        /// Default: 1.49f
        /// </summary>
        public float DecayTime
        {
            get => GetEffectParameter(EffectFloat.EaxReverbDecayTime);
            set => SetEffectParameter(EffectFloat.EaxReverbDecayTime, value);
        }
        /// <summary>
        /// Decay HF Ratio scales the decay time of high frequencies relative to the value
        /// of the Decay Time property. By changing this value, you are changing the amount
        /// of time it takes for the high frequencies to decay compared to the mid frequencies
        /// of the reverb.
        /// Range [0.1f .. 2.0f]
        /// Default: 0.83f
        /// </summary>
        public float DecayHighFreqRatio
        {
            get => GetEffectParameter(EffectFloat.EaxReverbDecayHFRatio);
            set => SetEffectParameter(EffectFloat.EaxReverbDecayHFRatio, value);
        }
        /// <summary>
        /// Decay LF Ratio scales the decay time of low frequencies in the reverberation
        /// in the same manner that Decay HF Ratio handles high frequencies. Unit: Linear
        /// multiplier
        /// Range [0.1f .. 2.0f]
        /// Default: 1.0f
        /// </summary>
        public float DecayLowFreqRatio
        {
            get => GetEffectParameter(EffectFloat.EaxReverbDecayLFRatio);
            set => SetEffectParameter(EffectFloat.EaxReverbDecayLFRatio, value);
        }
        /// <summary>
        /// Reflections Gain sets the level of the early reflections in an environment. Early
        /// reflections are used as a cue for determining the size of the environment we
        /// are in.
        /// Unit: Linear gain
        /// Range [0.0f .. 3.16f]
        /// Default: 0.05f
        /// </summary>
        public float ReflectionsGain
        {
            get => GetEffectParameter(EffectFloat.EaxReverbReflectionsGain);
            set => SetEffectParameter(EffectFloat.EaxReverbReflectionsGain, value);
        }
        /// <summary>
        /// Reflections Delay controls the amount of time it takes for the first reflected
        /// wave front to reach the listener, relative to the arrival of the direct-path
        /// sound.
        /// Unit: Seconds
        /// Range [0.0f .. 0.3f]
        /// Default: 0.007f
        /// </summary>
        public float ReflectionsDelay
        {
            get => GetEffectParameter(EffectFloat.EaxReverbReflectionsDelay);
            set => SetEffectParameter(EffectFloat.EaxReverbReflectionsDelay, value);
        }
        /// <summary>
        /// This Vector3 controls the spatial distribution of the cluster of early reflections.
        /// The direction of this vector controls the global direction of the reflections,
        /// while its magnitude controls how focused the reflections are towards this direction.
        /// For legacy reasons this Vector3 follows a left-handed co-ordinate system! Note
        /// that OpenAL uses a right-handed coordinate system.
        /// Unit: Vector3 of length 0f to 1f 
        /// Default: {0.0f, 0.0f, 0.0f}
        /// </summary>
        public Vector3 ReflectionsPan
        {
            get => GetEffectParameter(EffectVector3.EaxReverbReflectionsPan);
            set => SetEffectParameter(EffectVector3.EaxReverbReflectionsPan, value);
        }
        /// <summary>
        /// The Late Reverb Gain property controls the overall amount of later reverberation
        /// relative to the Gain property.
        /// Range [0.0f .. 10.0f]
        /// Default: 1.26f
        /// </summary>
        public float LateReverbGain
        {
            get => GetEffectParameter(EffectFloat.EaxReverbLateReverbGain);
            set => SetEffectParameter(EffectFloat.EaxReverbLateReverbGain, value);
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
            get => GetEffectParameter(EffectFloat.EaxReverbLateReverbDelay);
            set => SetEffectParameter(EffectFloat.EaxReverbLateReverbDelay, value);
        }
        /// <summary>
        /// Reverb Pan does for the Reverb what Reflections Pan does for the Reflections.
        /// Unit: Vector3 of length 0f to 1f
        /// Default: {0.0f, 0.0f, 0.0f}
        /// </summary>
        public Vector3 LateReverbPan
        {
            get => GetEffectParameter(EffectVector3.EaxReverbLateReverbPan);
            set => SetEffectParameter(EffectVector3.EaxReverbLateReverbPan, value);
        }
        /// <summary>
        /// Echo Time controls the rate at which the cyclic echo repeats itself along the reverberation decay.
        /// Range [0.075f .. 0.25f]
        /// Default: 0.25f
        /// </summary>
        public float EchoTime
        {
            get => GetEffectParameter(EffectFloat.EaxReverbEchoTime);
            set => SetEffectParameter(EffectFloat.EaxReverbEchoTime, value);
        }
        /// <summary>
        /// Echo Depth introduces a cyclic echo in the reverberation decay, which will be
        /// noticeable with transient or percussive sounds.
        /// Range [0.0f .. 1.0f]
        /// Default: 0.0f
        /// </summary>
        public float EchoDepth
        {
            get => GetEffectParameter(EffectFloat.EaxReverbEchoDepth);
            set => SetEffectParameter(EffectFloat.EaxReverbEchoDepth, value);
        }
        /// <summary>
        /// Modulation Time controls the speed of the rate of periodic changes in pitch (vibrato).
        /// Range [0.04f .. 4.0f]
        /// Default: 0.25f
        /// </summary>
        public float ModulationTime
        {
            get => GetEffectParameter(EffectFloat.EaxReverbModulationTime);
            set => SetEffectParameter(EffectFloat.EaxReverbModulationTime, value);
        }
        /// <summary>
        /// Modulation Depth controls the amount of pitch change. Low values of Diffusion
        /// will contribute to reinforcing the perceived effect by reducing the mixing of
        /// overlapping reflections in the reverberation decay.
        /// Range [0.0f .. 1.0f]
        /// Default: 0.0f
        /// </summary>
        public float ModulationDepth
        {
            get => GetEffectParameter(EffectFloat.EaxReverbModulationDepth);
            set => SetEffectParameter(EffectFloat.EaxReverbModulationDepth, value);
        }
        /// <summary>
        /// The Air Absorption Gain HF property controls the distance-dependent attenuation
        /// at high frequencies caused by the propagation medium. It applies to reflected
        /// sound only.
        /// Range [0.892f .. 1.0f]
        /// Default: 0.994f
        /// </summary>
        public float AirAbsorptionGainHighFreq
        {
            get => GetEffectParameter(EffectFloat.EaxReverbAirAbsorptionGainHF);
            set => SetEffectParameter(EffectFloat.EaxReverbAirAbsorptionGainHF, value);
        }
        /// <summary>
        /// The property HF reference determines the frequency at which the high-frequency
        /// effects created by Reverb properties are measured. Unit: Hz Range [1000.0f .. 20000.0f] Default: 5000.0f
        /// </summary>
        public float HighFreqReference
        {
            get => GetEffectParameter(EffectFloat.EaxReverbHFReference);
            set => SetEffectParameter(EffectFloat.EaxReverbHFReference, value);
        }
        /// <summary>
        /// The property LF reference determines the frequency at which the low-frequency
        /// effects created by Reverb properties are measured.
        /// Unit: Hz
        /// Range [20.0f .. 1000.0f]
        /// Default: 250.0f
        /// </summary>
        public float LowFreqReference
        {
            get => GetEffectParameter(EffectFloat.EaxReverbLFReference);
            set => SetEffectParameter(EffectFloat.EaxReverbLFReference, value);
        }
        /// <summary>
        /// The Room Rolloff Factor property is one of two methods available to attenuate
        /// the reflected sound (containing both reflections and reverberation) according
        /// to source-listener distance. It's defined the same way as OpenAL Rolloff Factor,
        /// but operates on reverb sound instead of direct-path sound.
        /// Range [0.0f .. 10.0f]
        /// Default: 0.0f
        /// </summary>
        public float RoomRolloffFactor
        {
            get => GetEffectParameter(EffectFloat.EaxReverbRoomRolloffFactor);
            set => SetEffectParameter(EffectFloat.EaxReverbRoomRolloffFactor, value);
        }
        /// <summary>
        /// When this flag is set, the high-frequency decay time automatically stays below
        /// a limit value that's derived from the setting of the property AirAbsorptionGainHF.
        /// Unit: (0) False, (1) True
        /// Range [False, True]
        /// Default: True
        /// </summary>
        public bool DecayHighFreqLimit
        {
            get => GetEffectParameter(EffectInteger.EaxReverbDecayHFLimit) != 0;
            set => SetEffectParameter(EffectInteger.EaxReverbDecayHFLimit, value ? 1 : 0);
        }
    }
}