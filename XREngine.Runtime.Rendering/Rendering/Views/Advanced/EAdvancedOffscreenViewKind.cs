namespace XREngine.Rendering;

/// <summary>
/// Categories of secondary and offscreen render views in the Advanced Render Pipeline.
/// </summary>
public enum EAdvancedOffscreenViewKind : uint
{
    SceneCapture = 0u,
    Mirror = 1u,
    Portal = 2u,
    ReflectionProbe = 3u,
    Thumbnail = 4u,
    DepthOnly = 5u,
    VisibilityOnly = 6u,
}
