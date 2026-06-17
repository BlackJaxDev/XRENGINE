namespace XREngine.Rendering.Resources;

/// <summary>
/// Registry record for a logical data-buffer resource and the concrete buffer currently backing it.
/// </summary>
public sealed class RenderBufferResource
{
    /// <summary>
    /// Describes buffer size, target, usage, aliasing, and access characteristics.
    /// </summary>
    public BufferResourceDescriptor Descriptor { get; private set; }

    /// <summary>
    /// Live data-buffer object bound to the descriptor, or <c>null</c> when not created.
    /// </summary>
    public XRDataBuffer? Instance { get; private set; }

    /// <summary>
    /// Creates a data-buffer registry record from its logical descriptor.
    /// </summary>
    internal RenderBufferResource(BufferResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>
    /// Replaces the descriptor used by future buffer planning.
    /// </summary>
    public void UpdateDescriptor(BufferResourceDescriptor descriptor)
        => Descriptor = descriptor;

    /// <summary>
    /// Associates a live data buffer with this logical resource.
    /// </summary>
    public void Bind(XRDataBuffer buffer)
    {
        if (Instance == buffer)
            return;

        Instance = buffer;
    }

    /// <summary>
    /// Destroys the live buffer while keeping the descriptor record intact.
    /// </summary>
    public void DestroyInstance()
    {
        Instance?.Destroy(true);
        Instance = null;
    }
}
