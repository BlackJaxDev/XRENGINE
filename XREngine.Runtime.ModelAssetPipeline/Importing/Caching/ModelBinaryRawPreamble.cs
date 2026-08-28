namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Fixed fields parsed before string-pool references can be resolved.
/// </summary>
internal sealed class ModelBinaryRawPreamble
{
    public uint Flags { get; init; }
    public ulong FileSize { get; init; }
    public ulong HeaderChecksum { get; init; }
    public ulong StringPoolOffset { get; init; }
    public ulong StringPoolLength { get; init; }
    public ulong ChunkTableOffset { get; init; }
    public ulong ChunkTableLength { get; init; }
    public ulong ChunkTableChecksum { get; init; }
    public ulong StringPoolChecksum { get; init; }
    public uint ChunkCount { get; init; }
    public ulong EntrySourceLength { get; init; }
    public long EntrySourceLastWriteUtcTicks { get; init; }
    public ulong EntrySourceHash { get; init; }
    public ModelBinarySourceHashMode EntrySourceHashMode { get; init; }
    public ModelBinaryAssetType AssetType { get; init; }
    public ModelBinaryHash128 RequestedPolicyHash { get; init; }
    public ModelBinaryHash128 BackendResolutionHash { get; init; }
    public ModelBinaryHash128 ActualBackendKeyHash { get; init; }
    public uint ActualBackendVersion { get; init; }
    public uint ActualBackendIdOffset { get; init; }
    public ModelBinaryHash128 VariantFingerprint { get; init; }
    public ModelBinaryHash128 ImportOptionsHash { get; init; }
    public ModelBinaryHash128 ModelCookSettingsHash { get; init; }
    public ModelBinaryHash128 DependencyManifestHash { get; init; }
    public uint DependencyCount { get; init; }
    public uint MaterialPolicyVersion { get; init; }
    public uint SourceIdentityOffset { get; init; }
    public ulong EngineBuildIdentity { get; init; }
}
