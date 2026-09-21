namespace XREngine.Rendering;

/// <summary>
/// Immutable engine-level identity of a deferred desktop presentation source.
/// </summary>
public readonly record struct WindowPresentationSourceMarker(
    XRTexture? SourceTexture,
    XRFrameBuffer? SourceFrameBuffer,
    IWindowPresentationBindingPublisher? Publisher,
    ulong PublicationToken)
{
    /// <summary>Gets whether this request explicitly represents desktop presentation.</summary>
    public bool HasSource => SourceTexture is not null;

    /// <summary>Gets whether this marker identifies one exact deferred presentation publication.</summary>
    public bool HasDeferredAuthority => Publisher is not null && PublicationToken != 0;
}
