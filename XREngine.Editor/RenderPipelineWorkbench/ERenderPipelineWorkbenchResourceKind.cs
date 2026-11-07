namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Identifies the kind of a logical render graph resource name.
/// </summary>
public enum ERenderPipelineWorkbenchResourceKind
{
    /// <summary>The name has no known prefix.</summary>
    Other,
    /// <summary>A named texture (<c>tex::Name</c>).</summary>
    Texture,
    /// <summary>A framebuffer attachment (<c>fbo::Name::color</c>, <c>::depth</c>, or <c>::stencil</c>).</summary>
    FrameBufferAttachment,
    /// <summary>The output render target.</summary>
    Output,
}
