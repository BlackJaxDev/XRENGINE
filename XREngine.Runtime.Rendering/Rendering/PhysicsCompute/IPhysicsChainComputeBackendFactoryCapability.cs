namespace XREngine.Rendering.Compute;

/// <summary>
/// Backend capability that creates the renderer-specific physics-chain compute adapter.
/// </summary>
public interface IPhysicsChainComputeBackendFactoryCapability
{
    bool TryCreatePhysicsChainComputeBackend(out IPhysicsChainComputeBackend? backend);
}
