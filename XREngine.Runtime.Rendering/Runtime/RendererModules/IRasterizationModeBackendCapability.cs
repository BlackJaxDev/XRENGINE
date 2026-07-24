namespace XREngine.Rendering;

/// <summary>
/// Provides scoped rasterization-mode changes without exposing a native graphics API.
/// </summary>
public interface IRasterizationModeBackendCapability
{
    void SetWireframeRasterization(bool enabled);
}
