namespace XREngine.Rendering.Commands;

/// <summary>
/// Generation-checked canonical draw identity captured with the exact shared
/// scene publication that owns its reclamation lifetime.
/// </summary>
public readonly record struct AdvancedGpuSceneDrawIdentity(
    AdvancedSharedGpuSceneDatabase? Database,
    AdvancedGpuScenePublicationReference Publication,
    AdvancedGpuHandle Handle)
{
    public ulong DatabaseEpoch => Publication.DatabaseEpoch;

    public bool IsValid
        => Database is not null &&
           Publication.IsValid &&
           Publication.DatabaseEpoch == Database.DatabaseEpoch &&
           Handle.IsValid;
}
