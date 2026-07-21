namespace XREngine.Components;

/// <summary>Independent CPU publication policy applied without re-registering state.</summary>
public readonly record struct PhysicsChainCpuOutputPolicy(
    PhysicsChainOutputCadence PaletteCadence,
    PhysicsChainOutputCadence BoundsCadence,
    PhysicsChainOutputCadence TransformMirrorCadence)
{
    public static PhysicsChainCpuOutputPolicy EverySimulationStep => new(
        PhysicsChainOutputCadence.EverySimulationStep,
        PhysicsChainOutputCadence.EverySimulationStep,
        PhysicsChainOutputCadence.EverySimulationStep);

    public static PhysicsChainCpuOutputPolicy FromQuality(in PhysicsChainQualityPolicy policy)
        => new(policy.PaletteCadence, policy.BoundsCadence, policy.TransformMirrorCadence);

    public bool IsValid
        => Enum.IsDefined(PaletteCadence)
            && Enum.IsDefined(BoundsCadence)
            && Enum.IsDefined(TransformMirrorCadence);
}
