namespace XREngine.Components;

/// <summary>Explicit cadence for compatibility hierarchy mirroring.</summary>
public readonly record struct PhysicsChainCpuMirrorPolicy(bool Enabled, int IntervalFrames)
{
    public static PhysicsChainCpuMirrorPolicy Disabled => new(false, 0);
    public static PhysicsChainCpuMirrorPolicy EveryFrame => new(true, 1);

    public bool IsValid => !Enabled || IntervalFrames > 0;

    internal int NormalizedInterval => Enabled ? Math.Max(IntervalFrames, 1) : int.MaxValue;
}
