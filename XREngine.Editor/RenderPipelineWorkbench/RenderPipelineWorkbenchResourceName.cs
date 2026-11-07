namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The parts of a logical render graph resource name.
/// </summary>
/// <param name="Kind">The kind of resource.</param>
/// <param name="BaseName">The texture or framebuffer name without the prefix and the attachment suffix.</param>
/// <param name="Attachment">The attachment suffix (<c>color</c>, <c>color2</c>, <c>depth</c>, <c>stencil</c>), or an empty string.</param>
public readonly record struct RenderPipelineWorkbenchResourceName(
    ERenderPipelineWorkbenchResourceKind Kind,
    string BaseName,
    string Attachment);
