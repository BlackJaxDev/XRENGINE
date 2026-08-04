namespace XREngine.Components
{
    public sealed class Audio2Face3DNativeBridgeSessionConfig
    {
        public int InputSampleRate { get; init; } = Audio2Face3DNativeBridge.DefaultInputSampleRate;
        public bool EnableEmotion { get; init; } = true;
        public string FaceModelPath { get; init; } = string.Empty;
        public string EmotionModelPath { get; init; } = string.Empty;
    }
}