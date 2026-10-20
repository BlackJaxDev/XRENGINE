namespace XREngine.Rendering.Compute;

/// <summary>Identifies the exact request and reset state behind a renderer palette.</summary>
public readonly record struct PhysicsChainSpatialSourceIdentity(
    int RequestId, int ExecutionGeneration, int StaticDataVersion,
    int ParticleStateVersion, int BindingGeneration, long BoneBufferGeneration);
