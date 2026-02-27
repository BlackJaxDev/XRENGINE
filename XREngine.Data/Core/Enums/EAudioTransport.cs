namespace XREngine
{
    /// <summary>
    /// Audio transport backend selection for the cascading settings system.
    /// Maps to <c>AudioTransportType</c> in the audio runtime layer.
    /// </summary>
    public enum EAudioTransport
    {
        /// <summary>Hardware-accelerated via OpenAL Soft. Default path.</summary>
        OpenAL,

        /// <summary>Managed software mixer via NAudio. No native OpenAL dependency.</summary>
        NAudio,
    }
}
