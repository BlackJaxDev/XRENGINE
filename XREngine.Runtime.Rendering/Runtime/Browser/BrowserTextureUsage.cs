namespace XREngine.Rendering;

/// <summary>Baseline texture usages with the WebGPU bit encoding.</summary>
[Flags]
public enum BrowserTextureUsage
{
    CopySource = 1,
    CopyDestination = 2,
    TextureBinding = 4,
    RenderAttachment = 16
}
