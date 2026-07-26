namespace XREngine.Rendering;

/// <summary>
/// Null object used when no editor scene-panel presentation adapter is installed.
/// </summary>
internal sealed class UninstalledRuntimeWindowScenePanelAdapter : IRuntimeWindowScenePanelAdapter
{
    public static UninstalledRuntimeWindowScenePanelAdapter Instance { get; } = new();

    public XRTexture2D? Texture => null;
    public XRFrameBuffer? FrameBuffer => null;

    public void Dispose()
    {
    }

    public void InvalidateResources()
    {
    }

    public void InvalidateResourcesImmediate()
    {
    }

    public void OnFramebufferResized(IRuntimeRenderWindowHost window, int framebufferWidth, int framebufferHeight)
    {
    }

    public bool TryRenderScenePanelMode(IRuntimeRenderWindowHost window)
        => false;

    public void EndScenePanelMode(IRuntimeRenderWindowHost window)
    {
    }
}
