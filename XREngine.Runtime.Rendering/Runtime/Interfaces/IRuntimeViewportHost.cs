namespace XREngine.Rendering;

/// <summary>
/// Runtime viewport dimensions exposed to code that should not depend on the concrete viewport type.
/// </summary>
public interface IRuntimeViewportHost
{
    /// <summary>
    /// Gets the viewport presentation width in pixels.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Gets the viewport presentation height in pixels.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Gets the internal render width in pixels, which may differ from presentation width when upscaling is active.
    /// </summary>
    int InternalWidth { get; }

    /// <summary>
    /// Gets the internal render height in pixels, which may differ from presentation height when upscaling is active.
    /// </summary>
    int InternalHeight { get; }
}
