namespace XREngine.Components;

/// <summary>World-owned CPU outputs requested by registered consumers.</summary>
[Flags]
public enum PhysicsChainCpuConsumerFlags : byte
{
    None = 0,
    Palette = 1 << 0,
    Bounds = 1 << 1,
    TransformMirror = 1 << 2,
}
