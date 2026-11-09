namespace XREngine.Rendering.Commands;

public sealed partial class BackendReadyFramePackage
{
    /// <summary>
    /// Seals an unpublished package against one imported-resource commit.
    /// Command membership, collection identity, and canonical data stay frozen.
    /// </summary>
    internal bool TryFinalizeImportedResourceIdentity(
        in BackendReadyFramePackageIdentity expected,
        int descriptorRevision,
        int instanceRevision)
    {
        if (State != EBackendReadyFramePackageState.Prepared || Identity != expected)
            return false;

        Identity = expected with
        {
            DescriptorGeneration = descriptorRevision,
            ResourceInstanceRevision = instanceRevision,
        };
        return true;
    }
}
