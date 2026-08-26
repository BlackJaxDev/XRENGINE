namespace XREngine.Components;

/// <summary>Optional authored override for one independently safe output axis.</summary>
public enum PhysicsChainOutputControl : byte
{
    InheritTier,
    EverySimulationStep,
    Hold,
}
