using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Holds one pending result and its exact source leases until delivery or
/// cancellation. The pipeline serializes every field change under its lock.
/// </summary>
internal sealed class EditorPickingRequestSlot
{
    public EEditorPickingRequestState State;
    public ulong Generation;
    public AdvancedPickingQuery Query;
    public int ResourceGeneration;
    public BackendReadyFramePackageIdentity PackageIdentity;
    public long PackageGeneration;
    public XRTexture? IdentityTexture;
    public EditorPickingSnapshotLease SidecarLease;
    public AdvancedGpuScenePublicationLease NativeLease;
    public Action<EditorPickingResult>? Completed;
    public bool SuppressDelivery;

    public void Clear()
    {
        State = EEditorPickingRequestState.Free;
        Generation = 0UL;
        Query = default;
        ResourceGeneration = 0;
        PackageIdentity = default;
        PackageGeneration = 0L;
        IdentityTexture = null;
        SidecarLease = default;
        NativeLease = default;
        Completed = null;
        SuppressDelivery = false;
    }
}
