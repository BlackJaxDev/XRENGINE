namespace XREngine;

/// <summary>
/// Attachment aspect whose completed write satisfies an exact output request.
/// Exact completion requests name one aspect so depth publication cannot be
/// mistaken for color publication.
/// </summary>
public enum ERenderOutputWriteAspect : byte
{
    Color,
    Depth,
    Stencil,
}
