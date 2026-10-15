namespace XREngine.Rendering;

/// <summary>Defers texture destruction until all counted bindings end.</summary>
internal sealed class XRTextureBindingLease : RenderResourceLeaseOwner
{
    private readonly XRTexture _texture;

    public XRTextureBindingLease(XRTexture texture)
        => _texture = texture;

    public void Retire()
        => RetireAuthoringResources();

    protected override void DisposeRetainedResources()
        => _texture.Destroy(true);
}
