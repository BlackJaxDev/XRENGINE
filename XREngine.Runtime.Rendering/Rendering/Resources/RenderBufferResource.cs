namespace XREngine.Rendering.Resources;

/// <summary>
/// Registry record for a logical data-buffer resource and the concrete buffer currently backing it.
/// </summary>
public sealed class RenderBufferResource
{
    private readonly object _bindingLock = new();
    private XRDataBuffer? _instance;
    private bool _ownsInstance = true;

    /// <summary>
    /// Describes buffer size, target, usage, aliasing, and access characteristics.
    /// </summary>
    public BufferResourceDescriptor Descriptor { get; private set; }

    /// <summary>
    /// Live data-buffer object bound to the descriptor, or <c>null</c> when not created.
    /// </summary>
    public XRDataBuffer? Instance => Volatile.Read(ref _instance);

    public bool OwnsInstance => Volatile.Read(ref _ownsInstance);

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
    public void Bind(XRDataBuffer buffer, bool ownsInstance = true)
        => BindCore(buffer, ownsInstance, transferRetiringUse: false);

    /// <summary>Transfers an existing counted use from staged imported bindings.</summary>
    internal void BindTransferred(XRDataBuffer buffer, bool ownsInstance)
        => BindCore(buffer, ownsInstance, transferRetiringUse: true);

    private void BindCore(XRDataBuffer buffer, bool ownsInstance, bool transferRetiringUse)
    {
        XRDataBuffer? previous;
        lock (_bindingLock)
        {
            if (ReferenceEquals(_instance, buffer) && _ownsInstance == ownsInstance)
                return;

            previous = _instance;
            bool ownedPrevious = _ownsInstance;
            if (!ReferenceEquals(previous, buffer))
            {
                if (transferRetiringUse)
                    buffer.RetainBindingUse();
                else if (!buffer.TryRetainActiveBindingUse())
                    throw new ObjectDisposedException(nameof(XRDataBuffer));
            }

            Volatile.Write(ref _instance, buffer);
            Volatile.Write(ref _ownsInstance, ownsInstance);
            if (ownedPrevious && !ReferenceEquals(previous, buffer))
                previous?.RetireBindingStorage();
        }

        if (!ReferenceEquals(previous, buffer))
            previous?.ReleaseBindingUse();
    }

    /// <summary>Returns the current buffer with one counted binding use.</summary>
    public bool TryRetainBinding(out XRDataBuffer? buffer)
    {
        lock (_bindingLock)
        {
            buffer = _instance;
            if (buffer is null)
                return false;

            buffer.RetainBindingUse();
            return true;
        }
    }

    /// <summary>
    /// Destroys the live buffer while keeping the descriptor record intact.
    /// </summary>
    public void DestroyInstance()
    {
        XRDataBuffer? previous;
        lock (_bindingLock)
        {
            previous = _instance;
            bool ownedPrevious = _ownsInstance;
            Volatile.Write(ref _instance, null);
            Volatile.Write(ref _ownsInstance, true);
            if (ownedPrevious)
                previous?.RetireBindingStorage();
        }

        previous?.ReleaseBindingUse();
    }
}
