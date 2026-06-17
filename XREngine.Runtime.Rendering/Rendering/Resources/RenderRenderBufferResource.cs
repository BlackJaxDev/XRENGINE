namespace XREngine.Rendering.Resources;

/// <summary>
/// Registry record for a logical renderbuffer resource and its live renderbuffer instance.
/// </summary>
public sealed class RenderRenderBufferResource
{
    /// <summary>
    /// Describes renderbuffer storage, size, multisampling, and default framebuffer attachment.
    /// </summary>
    public RenderBufferResourceDescriptor Descriptor { get; private set; }

    /// <summary>
    /// Live renderbuffer object bound to the descriptor, or <c>null</c> when not created.
    /// </summary>
    public XRRenderBuffer? Instance { get; private set; }

    /// <summary>
    /// Creates a renderbuffer registry record from its logical descriptor.
    /// </summary>
    internal RenderRenderBufferResource(RenderBufferResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>
    /// Replaces the descriptor used by future renderbuffer planning.
    /// </summary>
    public void UpdateDescriptor(RenderBufferResourceDescriptor descriptor)
        => Descriptor = descriptor;

    /// <summary>
    /// Associates a live renderbuffer with this logical resource.
    /// </summary>
    public void Bind(XRRenderBuffer renderBuffer)
    {
        if (Instance == renderBuffer)
            return;

        Instance = renderBuffer;
    }

    /// <summary>
    /// Destroys the live renderbuffer while keeping the descriptor record intact.
    /// </summary>
    public void DestroyInstance()
    {
        Instance?.Destroy(true);
        Instance = null;
    }
}
