namespace XREngine.Components;

/// <summary>Explicit runtime path selected for one physics-chain component.</summary>
public enum PhysicsChainRuntimeBackend : byte
{
    CpuDataOriented,
    GpuBatched,
    GpuStandalone,
}
