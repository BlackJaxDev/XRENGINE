namespace XREngine.Rendering.Compute;

/// <summary>Retains a possible tighter resident bound until its producer completes.</summary>
internal readonly record struct PhysicsChainResidentSpatialCheckpoint(
    ulong Revision, PhysicsChainPaletteSpatialState State, bool IsValid);
