namespace XREngine.Rendering.Commands;

/// <summary>
/// Render-buffer snapshot of one command's primitive handles and the exact
/// publication that makes them safe to consume. The handle set is immutable;
/// the publication reference changes only at a completed scene publication.
/// </summary>
public readonly record struct AdvancedGpuSceneDrawIdentitySnapshot(
    AdvancedSharedGpuSceneDatabase? Database,
    AdvancedGpuScenePublicationReference Publication,
    AdvancedGpuSceneDrawHandleSet? Handles)
{
    public AdvancedGpuSceneDrawIdentity Primary
        => new(Database, Publication, Handles?.Primary ?? AdvancedGpuHandle.Invalid);

    public bool IsValid => Primary.IsValid;

    public bool TryGetPrimitive(int primitiveIndex, out AdvancedGpuSceneDrawIdentity identity)
    {
        if ((uint)primitiveIndex >= (uint)(Handles?.Count ?? 0))
        {
            identity = default;
            return false;
        }

        identity = new(Database, Publication, Handles!.Handles[primitiveIndex]);
        return identity.IsValid;
    }
}
