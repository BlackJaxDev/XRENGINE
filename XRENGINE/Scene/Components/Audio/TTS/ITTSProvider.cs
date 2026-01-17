namespace XREngine.Components
{
    /// <summary>
    /// Interface for text-to-speech providers.
    /// </summary>
    public interface ITTSProvider
    {
        /// <summary>
        /// Synthesizes speech from the given text.
        /// </summary>
        /// <param name="text">The text to synthesize.</param>
        /// <param name="voice">Optional voice identifier (provider-specific).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The synthesis result containing audio data.</returns>
        Task<TTSResult> SynthesizeAsync(string text, string? voice = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the list of available voices for this provider.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Array of available voice information.</returns>
        Task<TTSVoice[]> GetAvailableVoicesAsync(CancellationToken cancellationToken = default);
    }
}
