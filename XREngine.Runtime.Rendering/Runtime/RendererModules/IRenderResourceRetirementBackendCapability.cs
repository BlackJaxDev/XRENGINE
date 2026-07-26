namespace XREngine.Rendering;

/// <summary>
/// Prepares backend references before stable pipeline resources are physically retired.
/// </summary>
public interface IRenderResourceRetirementBackendCapability
{
    void PrepareForPhysicalResourceDestruction(string reason);
}
