namespace XREngine
{
    public readonly struct JobProgress
    {
        public JobProgress(float value, object? payload = null)
        {
            Value = value;
            Payload = payload;
        }

        public float Value { get; }
        public object? Payload { get; }

        public static JobProgress FromRange(float completed, float total, object? payload = null)
        {
            var progress = total <= 0f ? 1f : completed / total;
            return new JobProgress(progress, payload);
        }
    }
}
