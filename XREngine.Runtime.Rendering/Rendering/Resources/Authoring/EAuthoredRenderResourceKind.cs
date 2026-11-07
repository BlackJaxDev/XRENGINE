namespace XREngine.Rendering.Resources.Authoring;

/// <summary>The kind of resource that an <see cref="AuthoredRenderResource"/> declares.</summary>
public enum EAuthoredRenderResourceKind
{
    /// <summary>A 2D texture, or a 2D texture array when it has more than one layer.</summary>
    Texture,

    /// <summary>A view of a mip or layer range of another authored texture.</summary>
    TextureView,

    /// <summary>A render buffer that is attached but never sampled.</summary>
    RenderBuffer,

    /// <summary>A GPU data buffer, such as a storage buffer.</summary>
    Buffer,

    /// <summary>A framebuffer that attaches other declared resources.</summary>
    FrameBuffer,
}
