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

    public void Reset(string newName, ProfilerScopeKind newKind)
    {
        Name = newName;
        Kind = newKind;
        Calls = 0;
        TotalMilliseconds = 0f;
        SelfMilliseconds = 0f;
        PeakMilliseconds = 0f;
    }

    public void Add(float total, float self)
    {
        Calls++;
        TotalMilliseconds += total;
        SelfMilliseconds += self;
        PeakMilliseconds = Math.Max(PeakMilliseconds, total);
    }
}
