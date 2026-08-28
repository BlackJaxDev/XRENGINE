namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable fixed-header inputs supplied by the model cooking layer.
/// </summary>
internal sealed class ModelBinaryCacheWriteHeader
{
    public ModelBinaryCacheWriteHeader(
        ulong entrySourceLength,
        long entrySourceLastWriteUtcTicks,
        ulong entrySourceHash,
        ModelBinarySourceHashMode entrySourceHashMode,
        ModelBinaryAssetType assetType,
        ModelBinaryHash128 requestedPolicyHash,
        ModelBinaryHash128 backendResolutionHash,
        string actualBackendId,
        uint actualBackendVersion,
        ModelBinaryHash128 variantFingerprint,
        ModelBinaryHash128 importOptionsHash,
        ModelBinaryHash128 modelCookSettingsHash,
        uint materialPolicyVersion,
        string sourceIdentity,
        ulong engineBuildIdentity = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actualBackendId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        if (entrySourceLastWriteUtcTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(entrySourceLastWriteUtcTicks));
        if (actualBackendVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(actualBackendVersion));

        EntrySourceLength = entrySourceLength;
        EntrySourceLastWriteUtcTicks = entrySourceLastWriteUtcTicks;
        EntrySourceHash = entrySourceHash;
        EntrySourceHashMode = entrySourceHashMode;
        AssetType = assetType;
        RequestedPolicyHash = requestedPolicyHash;
        BackendResolutionHash = backendResolutionHash;
        ActualBackendId = actualBackendId;
        ActualBackendKeyHash = ModelBinaryHash128.HashUtf8(actualBackendId);
        ActualBackendVersion = actualBackendVersion;
        VariantFingerprint = variantFingerprint;
        ImportOptionsHash = importOptionsHash;
        ModelCookSettingsHash = modelCookSettingsHash;
        MaterialPolicyVersion = materialPolicyVersion;
        SourceIdentity = sourceIdentity;
        EngineBuildIdentity = engineBuildIdentity;
    }

    public ulong EntrySourceLength { get; }
    public long EntrySourceLastWriteUtcTicks { get; }
    public ulong EntrySourceHash { get; }
    public ModelBinarySourceHashMode EntrySourceHashMode { get; }
    public ModelBinaryAssetType AssetType { get; }
    public ModelBinaryHash128 RequestedPolicyHash { get; }
    public ModelBinaryHash128 BackendResolutionHash { get; }
    public ModelBinaryHash128 ActualBackendKeyHash { get; }
    public string ActualBackendId { get; }
    public uint ActualBackendVersion { get; }
    public ModelBinaryHash128 VariantFingerprint { get; }
    public ModelBinaryHash128 ImportOptionsHash { get; }
    public ModelBinaryHash128 ModelCookSettingsHash { get; }
    public uint MaterialPolicyVersion { get; }
    public string SourceIdentity { get; }
    public ulong EngineBuildIdentity { get; }
}
