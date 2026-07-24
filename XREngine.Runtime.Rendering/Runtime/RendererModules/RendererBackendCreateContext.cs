namespace XREngine.Rendering;

/// <summary>
/// Stable input passed to all renderer backend factories.
/// </summary>
public readonly record struct RendererBackendCreateContext(
    IRuntimeRenderWindowHost Window,
    bool LinkRendererToWindow = true);
