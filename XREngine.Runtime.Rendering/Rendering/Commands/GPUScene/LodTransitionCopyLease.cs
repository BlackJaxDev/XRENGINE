namespace XREngine.Rendering.Commands;

/// <summary>Keeps both GPU copy buffers alive until the deferred copy ends.</summary>
internal sealed class LodTransitionCopyLease(
    XRDataBuffer source,
    XRDataBuffer destination) : IRenderResourceLeaseOwner
{
    public void RetainAuthoringUse()
    {
        source.RetainBindingUse();

        try
        {
            destination.RetainBindingUse();
        }
        catch
        {
            source.ReleaseBindingUse();
            throw;
        }
    }

    public void ReleaseAuthoringUse()
    {
        destination.ReleaseBindingUse();
        source.ReleaseBindingUse();
    }
}
