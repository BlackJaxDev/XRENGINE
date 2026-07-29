namespace XREngine.Rendering;

/// <summary>
/// View topology that participates in a shading-kernel cache key.
/// </summary>
public enum EAdvancedShaderViewMode : uint
{
    DesktopSingleView = 0,
    StereoArray = 1,
    MultiviewArray = 2,
}
