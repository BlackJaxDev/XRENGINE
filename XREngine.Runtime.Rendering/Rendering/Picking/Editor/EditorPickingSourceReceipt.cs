using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Retains the exact package and texture generation that produced editor identity.
/// The native publication is optional for frames with only authored draws.
/// </summary>
internal readonly struct EditorPickingSourceReceipt : IDisposable
{
    public EditorPickingSourceReceipt(
        int pipelineInstanceId,
        int resourceGeneration,
        XRTexture identityTexture,
        in EditorPickingSnapshotLease sidecarLease,
        in AdvancedGpuScenePublicationLease nativeLease)
    {
        PipelineInstanceId = pipelineInstanceId;
        ResourceGeneration = resourceGeneration;
        IdentityTexture = identityTexture;
        SidecarLease = sidecarLease;
        NativeLease = nativeLease;
        PackageIdentity = sidecarLease.PackageIdentity;
        PackageGeneration = sidecarLease.PackageGeneration;
    }

    public int PipelineInstanceId { get; }
    public int ResourceGeneration { get; }
    public XRTexture? IdentityTexture { get; }
    public EditorPickingSnapshotLease SidecarLease { get; }
    public AdvancedGpuScenePublicationLease NativeLease { get; }
    public BackendReadyFramePackageIdentity PackageIdentity { get; }
    public long PackageGeneration { get; }
    public bool IsValid => IdentityTexture is not null && SidecarLease.IsValid;

    public void Dispose()
    {
        SidecarLease.Dispose();
        NativeLease.Dispose();
    }
}
