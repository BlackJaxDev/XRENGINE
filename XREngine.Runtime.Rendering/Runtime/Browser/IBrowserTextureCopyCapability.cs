namespace XREngine.Rendering;

/// <summary>Submits an ordered same-format RGBA8 texture copy outside the frame packet.</summary>
public interface IBrowserTextureCopyCapability
{
    void CopyTexture(BrowserTextureCopyDescription copy);
}
