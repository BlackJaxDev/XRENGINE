namespace XREngine.Components
{
    public class STTResult
    {
        public bool Success { get; set; }
        public string Text { get; set; } = "";
        public float Confidence { get; set; }
        public bool IsFinal { get; set; }
        public string? Error { get; set; }
    }
} 