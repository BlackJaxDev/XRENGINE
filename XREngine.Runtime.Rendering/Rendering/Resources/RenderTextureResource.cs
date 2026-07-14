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
    /// <summary>
    /// Describes how the renderer should size, format, and use this texture.
    /// </summary>
    public TextureResourceDescriptor Descriptor { get; private set; }

    /// <summary>
    /// Live texture object bound to the descriptor, or <c>null</c> when only the descriptor exists.
    /// </summary>
    public XRTexture? Instance { get; private set; }

    public bool OwnsInstance { get; private set; } = true;

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
    {
        if (Instance == texture && OwnsInstance == ownsInstance)
            return;

        // The registry is an ownership map, not a lifetime boundary. The same logical
        // resource can be rebound between a texture and one of its views while render
        // resources are being enriched; destroying the previous instance here invalidates
        // live descriptors and framebuffer attachments.
        Instance = texture;
        OwnsInstance = ownsInstance;
    }

    /// <summary>
    /// Destroys the currently bound texture, if any, and leaves the descriptor available for rebuilds.
    /// </summary>
    public void DestroyInstance()
    {
        if (OwnsInstance)
            Instance?.Destroy(true);
        Instance = null;
        OwnsInstance = true;
    }
}
