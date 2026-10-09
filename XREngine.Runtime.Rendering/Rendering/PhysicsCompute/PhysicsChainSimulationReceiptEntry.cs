namespace XREngine.Rendering.Compute;

/// <summary>Records the inputs and spatial state consumed by one queued solve.</summary>
internal readonly record struct PhysicsChainSimulationReceiptEntry(
    GPUPhysicsChainRequest Request,
    ulong InputSequence,
    ulong SpatialRevision,
    PhysicsChainPaletteSpatialState TightSpatialState,
    bool TightSpatialStateValid);
