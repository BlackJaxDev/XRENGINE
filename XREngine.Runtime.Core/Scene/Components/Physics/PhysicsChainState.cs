namespace XREngine.Components;

/// <summary>
/// Backend-neutral ownership metadata for dynamic state. Actual particle
/// records remain backend-owned and are referenced by stable arena slices.
/// </summary>
public struct PhysicsChainState
{
    public PhysicsChainArenaSlice CurrentParticles { get; set; }
    public PhysicsChainArenaSlice PreviousParticles { get; set; }
    public PhysicsChainArenaSlice VelocityAndInertia { get; set; }
    public double SimulationClockSeconds { get; set; }
    public double FixedStepRemainderSeconds { get; set; }
    public PhysicsChainActivitySnapshot Activity { get; set; }
    public uint StateGeneration { get; set; }

    public readonly bool IsValid
        => CurrentParticles.IsValid
            && PreviousParticles.IsValid
            && VelocityAndInertia.IsValid
            && StateGeneration != 0u;

    public void ResetHistory()
    {
        PreviousParticles = CurrentParticles;
        FixedStepRemainderSeconds = 0.0;
    }
}
