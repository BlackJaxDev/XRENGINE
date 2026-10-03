namespace XREngine.Rendering;

public readonly partial record struct RendererBackendCreateContext
{
    public RendererBackendCreateContext(
        IRuntimeRenderWindowHost window,
        bool linkRendererToWindow = true,
        long moduleGeneration = 0)
        : this(new DesktopWindowRenderTarget(window), linkRendererToWindow, moduleGeneration)
    {
    }

    /// <summary>Gets the desktop host only for a desktop WSI target.</summary>
    public IRuntimeRenderWindowHost? Window
        => (Target as DesktopWindowRenderTarget)?.Window;

}
