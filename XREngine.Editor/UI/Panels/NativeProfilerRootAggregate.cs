using XREngine.Data.Profiling;

namespace XREngine.Editor;

/// <summary>Stores one root method's classified values for a complete CPU snapshot.</summary>
internal sealed class NativeProfilerRootAggregate(string name, ProfilerScopeKind kind)
{
    public string Name { get; private set; } = name;
    public ProfilerScopeKind Kind { get; private set; } = kind;
    public int Calls { get; private set; }
    public float TotalMilliseconds { get; private set; }
    public float SelfMilliseconds { get; private set; }
    public float PeakMilliseconds { get; private set; }
    public DateTime LastSeenUtc { get; private set; }
    public bool IsInactive { get; private set; }
    public bool WasObserved { get; private set; } = true;

    public void Reset(string newName, ProfilerScopeKind newKind)
    {
        Name = newName;
        Kind = newKind;
        Calls = 0;
        TotalMilliseconds = 0f;
        SelfMilliseconds = 0f;
        PeakMilliseconds = 0f;
        LastSeenUtc = default;
        IsInactive = false;
        WasObserved = true;
    }

    public void MarkSeen(DateTime now) => LastSeenUtc = now;

    public void Retain(NativeProfilerRootAggregate source, bool inactive)
    {
        Reset(source.Name, source.Kind);
        Calls = source.Calls;
        TotalMilliseconds = source.TotalMilliseconds;
        SelfMilliseconds = source.SelfMilliseconds;
        PeakMilliseconds = source.PeakMilliseconds;
        LastSeenUtc = source.LastSeenUtc;
        IsInactive = inactive;
        WasObserved = false;
    }

    public void Add(float total, float self)
    {
        Calls++;
        TotalMilliseconds += total;
        SelfMilliseconds += self;
        PeakMilliseconds = Math.Max(PeakMilliseconds, total);
    }
}
