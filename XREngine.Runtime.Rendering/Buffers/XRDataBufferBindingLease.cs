namespace XREngine.Rendering;

/// <summary>Defers buffer disposal until all counted bindings end.</summary>
internal sealed class XRDataBufferBindingLease : RenderResourceLeaseOwner
{
    private readonly XRDataBuffer _buffer;

    public XRDataBufferBindingLease(XRDataBuffer buffer)
        => _buffer = buffer;

    public void Retire()
        => RetireAuthoringResources();

    protected override void DisposeRetainedResources()
        => _buffer.Dispose();
}
