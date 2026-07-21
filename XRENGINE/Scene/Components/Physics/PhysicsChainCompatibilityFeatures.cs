namespace XREngine.Components;

/// <summary>Explicit compatibility or inspection work enabled for one chain.</summary>
[Flags]
public enum PhysicsChainCompatibilityFeatures : byte
{
    None = 0,
    CpuTransformMirror = 1 << 0,
    GpuBoneReadback = 1 << 1,
    PerChainDebugRendering = 1 << 2,
}
