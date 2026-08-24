namespace XREngine.Rendering.Commands;

/// <summary>
/// Opaque reference to a publication retained in the database's bounded ring.
/// It is safe to retain only while a corresponding lease is held.
/// </summary>
public readonly record struct AdvancedGpuScenePublicationReference(
    AdvancedGpuScenePublication Publication,
    AdvancedGpuScenePublicationSnapshot? Snapshot = null)
{
    public bool IsValid => Publication.IsValid;

    public ulong DatabaseEpoch => Publication.DatabaseEpoch;

    public ulong Sequence => Publication.Sequence;
}
