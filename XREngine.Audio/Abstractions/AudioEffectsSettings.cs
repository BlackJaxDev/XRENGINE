namespace XREngine.Audio
{
    /// <summary>
    /// Configuration passed to <see cref="IAudioEffectsProcessor.Initialize"/>.
    /// </summary>
    public sealed class AudioEffectsSettings
    {
        /// <summary>Output sample rate (Hz). Typically 44100 or 48000.</summary>
        public int SampleRate { get; set; } = 44100;

        /// <summary>Frame size in samples. Phonon default is 1024.</summary>
        public int FrameSize { get; set; } = 1024;
    }
}
