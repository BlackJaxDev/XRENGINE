using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources.Authoring;

/// <summary>One attachment of an authored framebuffer.</summary>
public sealed class AuthoredAttachment
{
    /// <summary>Gets or sets the name of the attached texture, texture view, or render buffer.</summary>
    [Pipelines.Commands.RenderResourceName]
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the attachment slot.</summary>
    public EFrameBufferAttachment Attachment { get; set; } = EFrameBufferAttachment.ColorAttachment0;

    /// <summary>Gets or sets the mip level to attach.</summary>
    public int MipLevel { get; set; }

    /// <summary>Gets or sets the layer to attach, or -1 to attach every layer.</summary>
    public int LayerIndex { get; set; } = -1;

    /// <summary>Returns a copy.</summary>
    public AuthoredAttachment Clone()
        => (AuthoredAttachment)MemberwiseClone();
}
