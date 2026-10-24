using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;

namespace XREngine.Editor;

/// <summary>Selects the highest thread time from each completed half-second CPU window.</summary>
internal sealed class NativeProfilerWorstFrameSelector
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(0.5);
    private Frame? _candidate;
    private Frame? _display;
    private DateTime _windowStart;
    private float _candidateMilliseconds;
    private long _sessionEpoch = long.MinValue;
    private long _publicationId = long.MinValue;

    public Frame? DisplayFrame => _display;
    public bool IsCompletedWindow { get; private set; }

    public void Observe(Frame? frame, DateTime now)
    {
        if (frame is null)
            return;
        if (frame.SessionEpoch != _sessionEpoch || (_windowStart != default && now < _windowStart))
        {
            Reset();
            _sessionEpoch = frame.SessionEpoch;
        }
        if (frame.Threads.Count == 0)
            return;
        if (frame.PublicationId == _publicationId)
            return;
        _publicationId = frame.PublicationId;
        if (_windowStart == default)
            _windowStart = now;

        float frameMilliseconds = 0f;
        for (int index = 0; index < frame.Threads.Count; index++)
            frameMilliseconds = Math.Max(frameMilliseconds, frame.Threads[index].TotalTimeMs);
        if (_candidate is null || frameMilliseconds > _candidateMilliseconds)
        {
            _candidate = frame;
            _candidateMilliseconds = frameMilliseconds;
        }

        if (now - _windowStart >= WindowDuration)
        {
            _display = _candidate;
            IsCompletedWindow = true;
            _candidate = frame;
            _candidateMilliseconds = frameMilliseconds;
            _windowStart = now;
        }
        else if (!IsCompletedWindow)
            _display = frame;
    }

    public void Reset()
    {
        _candidate = null;
        _display = null;
        _windowStart = default;
        _candidateMilliseconds = 0f;
        _sessionEpoch = long.MinValue;
        _publicationId = long.MinValue;
        IsCompletedWindow = false;
    }
}
