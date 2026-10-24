using System.Buffers;
using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;
using Node = XREngine.Engine.CodeProfiler.ProfilerNodeSnapshot;

namespace XREngine.Editor;

/// <summary>Groups recent FPS drops by thread and hottest scope path with bounded source work.</summary>
internal sealed class NativeProfilerSpikeHistory
{
    private const int MaximumEntries = 256;
    private const int MaximumThreadsPerFrame = 64;
    private const int MaximumPathChecks = 2048;
    private readonly List<NativeProfilerSpikeEntry> _entries = [];
    private readonly List<int> _sorted = [];
    private readonly Comparison<int> _compare;
    private readonly Dictionary<(int, string), int> _indices = [];
    private readonly List<string> _pathParts = [];
    private Frame? _lastFrame;

    public NativeProfilerSpikeHistory() => _compare = CompareEntries;

    public int Count => _entries.Count;
    public ulong Revision { get; private set; }
    public int LastSkippedThreads { get; private set; }
    public NativeProfilerSpikeEntry this[int index] => _entries[_sorted[index]];

    public bool Record(Frame? frame, Dictionary<int, float[]>? history,
        float minimumPreviousFps, float minimumDeltaMs, int baselineWindow, float minimumDropFps = 0f)
    {
        if (ReferenceEquals(frame, _lastFrame))
            return false;
        _lastFrame = frame;
        int previousSkippedThreads = LastSkippedThreads;
        LastSkippedThreads = 0;
        if (frame is null || history is null)
            return previousSkippedThreads != 0;
        int examined = Math.Min(frame.Threads.Count, MaximumThreadsPerFrame);
        LastSkippedThreads = frame.Threads.Count - examined;
        bool changed = LastSkippedThreads != previousSkippedThreads;
        for (int index = 0; index < examined; index++)
        {
            var thread = frame.Threads[index];
            if (!history.TryGetValue(thread.ThreadId, out float[]? samples) || samples.Length < 2)
                continue;
            float currentMs = samples[^1];
            float previousMs = samples[^2];
            if (!float.IsFinite(currentMs) || !float.IsFinite(previousMs) ||
                currentMs <= 0.0001f || previousMs <= 0.0001f ||
                1000f / previousMs < minimumPreviousFps || currentMs - previousMs < minimumDeltaMs)
                continue;
            float baselineMs = MedianTail(samples, baselineWindow);
            if (baselineMs <= 0.0001f)
                continue;
            float referenceFps = MathF.Min(1000f / previousMs, 1000f / baselineMs);
            float currentFps = 1000f / currentMs;
            float dropFps = referenceFps - currentFps;
            if (dropFps <= 0f || dropFps < minimumDropFps)
                continue;
            string path = HottestPath(thread.RootNodes, out bool truncated);
            var key = (thread.ThreadId, path);
            if (_indices.TryGetValue(key, out int existingIndex))
            {
                var existing = _entries[existingIndex];
                _entries[existingIndex] = dropFps > existing.WorstDropFps
                    ? existing with
                    {
                        SeenCount = existing.SeenCount + 1,
                        WorstFrameSeconds = frame.FrameTime,
                        WorstReferenceFps = referenceFps,
                        WorstCurrentFps = currentFps,
                        WorstDropFps = dropFps,
                    }
                    : existing with { SeenCount = existing.SeenCount + 1 };
            }
            else
            {
                if (_entries.Count == MaximumEntries)
                {
                    _indices.Remove((_entries[0].ThreadId, _entries[0].HotPath));
                    _entries.RemoveAt(0);
                    for (int old = 0; old < _entries.Count; old++)
                        _indices[(_entries[old].ThreadId, _entries[old].HotPath)] = old;
                }
                _indices[key] = _entries.Count;
                _entries.Add(new(thread.ThreadId, path, 1, frame.FrameTime,
                    referenceFps, currentFps, dropFps, truncated));
            }
            changed = true;
        }
        if (changed)
        {
            _sorted.Clear();
            for (int index = 0; index < _entries.Count; index++)
                _sorted.Add(index);
            _sorted.Sort(_compare);
            Revision++;
        }
        return changed;
    }

    public bool Clear()
    {
        if (_entries.Count == 0 && LastSkippedThreads == 0)
            return false;
        _entries.Clear();
        _sorted.Clear();
        _indices.Clear();
        _pathParts.Clear();
        LastSkippedThreads = 0;
        Revision++;
        return true;
    }

    public void Release()
    {
        _entries.Clear();
        _sorted.Clear();
        _indices.Clear();
        _pathParts.Clear();
        LastSkippedThreads = 0;
        _lastFrame = null;
    }

    private string HottestPath(IReadOnlyList<Node> roots, out bool truncated)
    {
        _pathParts.Clear();
        IReadOnlyList<Node> siblings = roots;
        int checks = 0;
        truncated = false;
        while (siblings.Count != 0)
        {
            Node? hottest = null;
            for (int index = 0; index < siblings.Count; index++)
            {
                if (++checks > MaximumPathChecks)
                {
                    truncated = true;
                    _pathParts.Add("(path exceeds 2048 checks)");
                    return string.Join(" > ", _pathParts);
                }
                Node candidate = siblings[index];
                if (hottest is null || candidate.ElapsedMs > hottest.ElapsedMs)
                    hottest = candidate;
            }
            if (hottest is null)
                break;
            _pathParts.Add(hottest.Name);
            siblings = hottest.Children;
        }
        return _pathParts.Count == 0 ? "(no scopes)" : string.Join(" > ", _pathParts);
    }

    private int CompareEntries(int left, int right)
        => _entries[right].WorstDropFps.CompareTo(_entries[left].WorstDropFps);

    private static float MedianTail(float[] samples, int requestedCount)
    {
        int count = Math.Min(Math.Max(1, requestedCount), samples.Length - 1);
        float[] buffer = ArrayPool<float>.Shared.Rent(count);
        try
        {
            Array.Copy(samples, samples.Length - 1 - count, buffer, 0, count);
            Array.Sort(buffer, 0, count);
            int middle = count / 2;
            return (count & 1) == 0 ? (buffer[middle - 1] + buffer[middle]) * 0.5f : buffer[middle];
        }
        finally { ArrayPool<float>.Shared.Return(buffer); }
    }
}
