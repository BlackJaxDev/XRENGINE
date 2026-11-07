namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>The kind of resource that a property marked with <see cref="RenderResourceNameAttribute"/> names.</summary>
public enum ERenderResourceNameKind
{
    /// <summary>Any declared resource.</summary>
    Any,

    /// <summary>A texture or texture view.</summary>
    Texture,

    /// <summary>A framebuffer.</summary>
    FrameBuffer,

    /// <summary>A data buffer.</summary>
    Buffer,

    /// <summary>A render buffer.</summary>
    RenderBuffer,
}
