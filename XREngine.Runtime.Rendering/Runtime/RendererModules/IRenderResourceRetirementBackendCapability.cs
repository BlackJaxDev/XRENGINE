namespace XREngine.Rendering;

/// <summary>
/// Prepares backend references before stable pipeline resources are physically retired.
/// </summary>
public interface IRenderResourceRetirementBackendCapability
{
    /// <summary>Whether retirement pressure may synchronously wait; event-loop backends poll on later frames.</summary>
    bool RequiresBlockingRetirementProgress => true;

    void PrepareForPhysicalResourceDestruction(string reason);
}
