namespace XREngine.Rendering.Resources;

/// <summary>
/// Defines who owns the lifetime of an imported render resource.
/// </summary>
public enum ExternalRenderResourceOwnership
{
    Window,
    Caller,
    Scene,
    XrRuntime,
    Backend,
}
