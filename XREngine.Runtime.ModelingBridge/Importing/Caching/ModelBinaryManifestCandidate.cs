
namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Stable backend candidate identity persisted in resolver attempt order.
/// </summary>
internal sealed class ModelBinaryManifestCandidate
{
    public ModelBinaryManifestCandidate(
        string stableId,
        uint implementationVersion,
        ModelImportBackendCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        if (implementationVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(implementationVersion));

        StableId = stableId;
        ImplementationVersion = implementationVersion;
        Capabilities = capabilities;
    }

    public string StableId { get; }
    public uint ImplementationVersion { get; }
    public ModelImportBackendCapabilities Capabilities { get; }
}
