namespace XREngine.Rendering.Resources;

/// <summary>
/// Registry record for a logical texture resource and the concrete texture currently backing it.
/// </summary>
/// <remarks>
/// The descriptor is the stable render-graph contract. The instance is the live engine object that
/// can be destroyed, recreated, or rebound as render resources are rebuilt.
/// </remarks>
public sealed class RenderTextureResource
{
    private readonly object _bindingLock = new();
    private XRTexture? _instance;
    private bool _ownsInstance = true;

    /// <summary>
    /// Describes how the renderer should size, format, and use this texture.
    /// </summary>
    public TextureResourceDescriptor Descriptor { get; private set; }

    /// <summary>
    /// Live texture object bound to the descriptor, or <c>null</c> when only the descriptor exists.
    /// </summary>
    public XRTexture? Instance => Volatile.Read(ref _instance);

    public bool OwnsInstance => Volatile.Read(ref _ownsInstance);

    /// <summary>
    /// Creates a texture registry record from its logical descriptor.
    /// </summary>
    internal RenderTextureResource(TextureResourceDescriptor descriptor)
        => Descriptor = descriptor;

    /// <summary>
    /// Replaces the logical descriptor without touching the currently bound texture instance.
    /// </summary>
    public void UpdateDescriptor(TextureResourceDescriptor descriptor)
        => Descriptor = descriptor;

    /// <summary>
    /// Associates a live texture with this logical resource.
    /// </summary>
    public void Bind(XRTexture texture, bool ownsInstance = true)
        => BindCore(texture, ownsInstance, transferRetiringUse: false);

    /// <summary>Transfers an existing counted use from staged imported bindings.</summary>
    internal void BindTransferred(XRTexture texture, bool ownsInstance)
        => BindCore(texture, ownsInstance, transferRetiringUse: true);

    private void BindCore(XRTexture texture, bool ownsInstance, bool transferRetiringUse)
    {
        XRTexture? previous;
        lock (_bindingLock)
        {
            if (ReferenceEquals(_instance, texture) && _ownsInstance == ownsInstance)
                return;

            previous = _instance;
            if (!ReferenceEquals(previous, texture))
            {
                if (transferRetiringUse)
                    texture.RetainBindingUse();
                else if (!texture.TryRetainActiveBindingUse())
                    throw new ObjectDisposedException(nameof(XRTexture));
            }

            // Rebinding can switch a texture to one of its views. Its former
            // owner decides when to retire it after dependent attachments end.
            Volatile.Write(ref _instance, texture);
            Volatile.Write(ref _ownsInstance, ownsInstance);
        }

        if (!ReferenceEquals(previous, texture))
            previous?.ReleaseBindingUse();
    }

    /// <summary>Returns the current texture with one counted binding use.</summary>
    public bool TryRetainBinding(out XRTexture? texture)
    {
        lock (_bindingLock)
        {
            texture = _instance;
            if (texture is null)
                return false;

            texture.RetainBindingUse();
            return true;
        }
    }

    /// <summary>
    /// Destroys the currently bound texture, if any, and leaves the descriptor available for rebuilds.
    /// </summary>
    public void DestroyInstance()
    {
        XRTexture? previous;
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
