namespace XREngine.Rendering;

/// <summary>
/// Adapter for rendering a native window's scene view into an editor scene panel.
/// </summary>
public interface IRuntimeWindowScenePanelAdapter : IDisposable
{
    /// <summary>
    /// Gets the scene panel color texture produced by the adapter, when panel presentation is active.
    /// </summary>
    XRTexture2D? Texture { get; }

    /// <summary>
    /// Gets the framebuffer that owns the scene panel render target, when panel presentation is active.
    /// </summary>
    XRFrameBuffer? FrameBuffer { get; }

    /// <summary>
    /// Marks scene panel GPU resources dirty so they are recreated before the next use.
    /// </summary>
    void InvalidateResources();

    /// <summary>
    /// Immediately releases and invalidates scene panel GPU resources.
    /// </summary>
    void InvalidateResourcesImmediate();

    /// <summary>
    /// Notifies the adapter that the owning window framebuffer changed size.
    /// </summary>
    void OnFramebufferResized(IRuntimeRenderWindowHost window, int framebufferWidth, int framebufferHeight);

    /// <summary>
    /// Attempts to begin rendering the supplied window through scene panel presentation.
    /// </summary>
    bool TryRenderScenePanelMode(IRuntimeRenderWindowHost window);

    /// <summary>
    /// Ends scene panel rendering mode for the supplied window.
    /// </summary>
    void EndScenePanelMode(IRuntimeRenderWindowHost window);
}
