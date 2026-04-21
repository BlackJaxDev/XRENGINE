namespace XREngine.Components
{
    public interface ISTTProvider
    {
        Task<STTResult> TranscribeAsync(byte[] audioData, int sampleRate, int bitsPerSample);
    }
} 