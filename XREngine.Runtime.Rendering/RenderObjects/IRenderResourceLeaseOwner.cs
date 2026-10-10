namespace XREngine.Rendering;

/// <summary>Keeps resources alive while a deferred render request uses them.</summary>
public interface IRenderResourceLeaseOwner
{
    void RetainAuthoringUse();
    void ReleaseAuthoringUse();
}
