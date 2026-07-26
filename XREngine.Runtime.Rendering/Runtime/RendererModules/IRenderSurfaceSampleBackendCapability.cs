namespace XREngine.Rendering;

/// <summary>
/// Provides the current render-surface sample count to backend-specific UI integrations.
/// </summary>
public interface IRenderSurfaceSampleBackendCapability
{
    int GetCurrentSurfaceSampleCount();
}
