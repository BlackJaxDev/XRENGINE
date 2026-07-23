namespace XREngine.Rendering;

/// <summary>
/// Immutable engine handle for a render-query descriptor. Recording state and
/// native slots belong to the active backend, never to this shared object.
/// </summary>
public sealed class XRRenderQuery : GenericRenderObject
{
    public XRRenderQuery()
        : this(RenderQueryDescriptor.ConservativeOcclusion)
    {
    }

    public XRRenderQuery(RenderQueryDescriptor descriptor)
        => Descriptor = descriptor;

    public RenderQueryDescriptor Descriptor { get; }
}
