namespace XREngine.Rendering.Resources;

/// <summary>
/// Registry record for a logical framebuffer resource and its live framebuffer instance.
/// </summary>
public sealed class RenderFrameBufferResource
{
    /// <summary>
    /// Describes framebuffer size and attachment wiring by logical resource name.
    /// </summary>
    public FrameBufferResourceDescriptor Descriptor { get; private set; }

    /// <summary>
    /// Live framebuffer object bound to the descriptor, or <c>null</c> until materialized.
    /// </summary>
    public XRFrameBuffer? Instance { get; private set; }

    /// <summary>
    /// Whether this record represents a physical render target rather than an
    /// attachmentless fullscreen-material helper stored in the FBO registry.
    /// </summary>
    public bool HasAttachments => Descriptor.Attachments.Count > 0;

    /// <summary>
    /// Creates a framebuffer registry record from its logical descriptor.
    /// </summary>
    internal RenderFrameBufferResource(FrameBufferResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>
    /// Replaces the descriptor used by future framebuffer planning.
    /// </summary>
    public void UpdateDescriptor(FrameBufferResourceDescriptor descriptor)
        => Descriptor = descriptor;

    /// <summary>
    /// Associates a live framebuffer with this logical resource.
    /// </summary>
    public void Bind(XRFrameBuffer frameBuffer)
    {
        if (Instance == frameBuffer)
            return;

        Instance = frameBuffer;
    }

    /// <summary>
    /// Destroys the live framebuffer while keeping the descriptor record intact.
    /// </summary>
    public void DestroyInstance()
    {
        Instance?.Destroy(true);
        Instance = null;
    }
}
