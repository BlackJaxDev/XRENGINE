using System.Diagnostics;

namespace XREngine.Audio
{
    /// <summary>
    /// Global audio subsystem configuration.
    /// Settings here are applied to <see cref="AudioManager"/> when the world starts or
    /// when the engine's cascading settings system pushes updated values at runtime.
    /// </summary>
    public static class AudioSettings
    {
        /// <summary>
        /// When <c>true</c>, the listener composes an output transport and effects processor.
        /// When <c>false</c> (default), it retains legacy OpenAL listener and source behavior
        /// through the registered spatial listener backend.
        /// </summary>
        public static bool AudioArchitectureV2 { get; set; } = false;

        /// <summary>
        /// Preferred audio transport backend. Takes effect on the next listener creation
        /// or when <see cref="ApplyTo"/> is called.
        /// </summary>
        public static EAudioTransport DefaultTransport { get; set; } = EAudioTransport.OpenAL;

        /// <summary>
        /// Preferred audio effects processor. Takes effect on the next listener creation
        /// or when <see cref="ApplyTo"/> is called.
        /// </summary>
        public static EAudioEffects DefaultEffects { get; set; } = EAudioEffects.OpenAL_EFX;

        /// <summary>
        /// Output sample rate in Hz. Takes effect on the next listener creation.
        /// </summary>
        public static int SampleRate { get; set; } = 44100;

        /// <summary>
        /// Pushes all current settings onto an <see cref="AudioManager"/>
        /// (transport, effects, sample rate, V2 flag).
        /// New listeners created after this call will use the updated values.
        /// </summary>
        public static void ApplyTo(AudioManager manager)
        {
            manager.DefaultTransport = DefaultTransport;
            manager.DefaultEffects = DefaultEffects;
            manager.SampleRate = SampleRate;
            Debug.WriteLine($"[AudioSettings] Applied: V2={AudioArchitectureV2}, Transport={DefaultTransport}, Effects={DefaultEffects}, SampleRate={SampleRate}");
        }
    }
}
