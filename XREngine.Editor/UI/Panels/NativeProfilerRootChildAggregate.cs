using XREngine.Data.Profiling;

namespace XREngine.Editor;

/// <summary>Stores one child path below an aggregated CPU root.</summary>
internal sealed class NativeProfilerRootChildAggregate(
    string name, ProfilerScopeKind kind, int rootIndex, int parentIndex, int depth)
{
    public string Name { get; private set; } = name;
    public ProfilerScopeKind Kind { get; private set; } = kind;
    public int RootIndex { get; private set; } = rootIndex;
    public int ParentIndex { get; private set; } = parentIndex;
    public int Depth { get; private set; } = depth;
    public int Calls { get; private set; }
    public float TotalMilliseconds { get; private set; }
    public float SelfMilliseconds { get; private set; }
    public float PeakMilliseconds { get; private set; }
    public DateTime LastSeenUtc { get; private set; }
    public bool IsInactive { get; private set; }

    public void Reset(string newName, ProfilerScopeKind newKind,
        int newRootIndex, int newParentIndex, int newDepth)
    {
        Name = newName;
        Kind = newKind;
        RootIndex = newRootIndex;
        ParentIndex = newParentIndex;
        Depth = newDepth;
        Calls = 0;
        TotalMilliseconds = 0f;
        SelfMilliseconds = 0f;
        PeakMilliseconds = 0f;
        LastSeenUtc = default;
        IsInactive = false;
    }

    public void MarkSeen(DateTime now) => LastSeenUtc = now;

    public void Retain(NativeProfilerRootChildAggregate source, int rootIndex, int parentIndex,
        bool inactive)
    {
        Reset(source.Name, source.Kind, rootIndex, parentIndex, source.Depth);
        Calls = source.Calls;
        TotalMilliseconds = source.TotalMilliseconds;
        SelfMilliseconds = source.SelfMilliseconds;
        PeakMilliseconds = source.PeakMilliseconds;
        LastSeenUtc = source.LastSeenUtc;
        IsInactive = inactive;
    }

    public void Add(float elapsed, float self)
    {
        Calls++;
        TotalMilliseconds += elapsed;
        SelfMilliseconds += self;
        PeakMilliseconds = Math.Max(PeakMilliseconds, elapsed);
    }
}
