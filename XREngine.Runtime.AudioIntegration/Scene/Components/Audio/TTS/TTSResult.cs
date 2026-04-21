namespace XREngine.Components
{
    /// <summary>
    /// Result of a text-to-speech synthesis operation.
    /// </summary>
    public class TTSResult
    {
        /// <summary>
        /// Whether the synthesis was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// The synthesized audio data in PCM format.
        /// </summary>
        public byte[]? AudioData { get; set; }

        /// <summary>
        /// The sample rate of the audio data in Hz.
        /// </summary>
        public int SampleRate { get; set; } = 24000;

        /// <summary>
        /// The number of audio channels (1 = mono, 2 = stereo).
        /// </summary>
        public int Channels { get; set; } = 1;

        /// <summary>
        /// The bits per sample (8, 16, or 32).
        /// </summary>
        public int BitsPerSample { get; set; } = 16;

        /// <summary>
        /// The audio format returned by the provider (e.g., "pcm", "mp3", "opus").
        /// </summary>
        public string AudioFormat { get; set; } = "pcm";

        /// <summary>
        /// Error message if the synthesis failed.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// The duration of the synthesized audio in seconds.
        /// </summary>
        public double DurationSeconds => AudioData != null && SampleRate > 0 && Channels > 0 && BitsPerSample > 0
            ? (double)AudioData.Length / (SampleRate * Channels * (BitsPerSample / 8))
            : 0;

        /// <summary>
        /// Creates a failed result with an error message.
        /// </summary>
        public static TTSResult Failure(string error) => new() { Success = false, Error = error };

        /// <summary>
        /// Creates a successful result with audio data.
        /// </summary>
        public static TTSResult FromPcm(byte[] audioData, int sampleRate = 24000, int channels = 1, int bitsPerSample = 16) => new()
        {
            Success = true,
            AudioData = audioData,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            AudioFormat = "pcm"
        };
    }

    /// <summary>
    /// Information about an available TTS voice.
    /// </summary>
    public class TTSVoice
    {
        /// <summary>
        /// The provider-specific voice identifier.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// The human-readable name of the voice.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The language code (e.g., "en-US", "es-ES").
        /// </summary>
        public string LanguageCode { get; set; } = string.Empty;

        /// <summary>
        /// The gender of the voice (if known).
        /// </summary>
        public EVoiceGender Gender { get; set; } = EVoiceGender.Neutral;

        /// <summary>
        /// Description of the voice characteristics.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// Gender of a TTS voice.
    /// </summary>
    public enum EVoiceGender
    {
        Neutral,
        Male,
        Female
    }
}
