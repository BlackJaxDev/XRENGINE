namespace XREngine.Rendering.Compute;

/// <summary>
/// Stable pass identities used by backend adapters for diagnostics and synchronization.
/// </summary>
public enum PhysicsChainComputePassKind
{
    ArenaGrowth,
    ActiveWorkReset,
    ActiveWorkCompaction,
    IndirectArgumentGeneration,
    Simulation,
    SelectiveReadbackGather,
    BoundsPublication,
    BonePalettePublication,
    ReadbackTransfer,
}
