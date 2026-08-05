namespace XREngine.Components
{
    /// <summary>
    /// The source mode for the Audio2Face3D component.
    /// </summary>
    public enum EAudio2Face3DSourceMode
    {
        /// <summary>
        /// The source mode is set to CSV playback, which means the audio data will be played back from a CSV file.
        /// </summary>
        CsvPlayback,
        /// <summary>
        /// The source mode is set to live stream, which means the audio data will be streamed in real-time from a live source.
        /// </summary>
        LiveStream,
    }
}
