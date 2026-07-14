namespace XREngine.Rendering.Resources;

/// <summary>
/// Identifies the concrete kind expected when an externally owned resource is
/// imported into a render-pipeline layout.
/// </summary>
public enum ExternalRenderResourceKind
{
    Texture,
    RenderBuffer,
    FrameBuffer,
    Buffer,
}
