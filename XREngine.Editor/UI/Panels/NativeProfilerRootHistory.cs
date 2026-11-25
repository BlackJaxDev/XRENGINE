namespace XREngine.Editor;

/// <summary>Keeps a fixed number of per-call root frequency samples.</summary>
internal sealed class NativeProfilerRootHistory
{
    public const int Capacity = 720;
    private readonly float[] _samples = new float[Capacity];
    private int _count;
    private int _next;

    public int LastSeenSerial { get; set; }
    public int Count => _count;

    public void Append(float hertz)
    {
        _samples[_next] = float.IsFinite(hertz) && hertz > 0f ? hertz : 0f;
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }

    public int CopyLatest(Span<float> destination, int requested)
    {
        int copied = Math.Min(Math.Min(_count, destination.Length), Math.Clamp(requested, 0, Capacity));
        int first = (_next - copied + Capacity) % Capacity;
        for (int index = 0; index < copied; index++)
            destination[index] = _samples[(first + index) % Capacity];
        return copied;
    }
}
