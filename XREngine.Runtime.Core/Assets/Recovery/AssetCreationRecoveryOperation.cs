namespace XREngine.Assets.Recovery;

/// <summary>
/// One journaled asset creation in a <see cref="AssetCreationRecoveryStore"/>. The store owns all
/// state changes. A caller holds this object as an Undo receipt.
/// </summary>
public sealed class AssetCreationRecoveryOperation
{
    internal AssetCreationRecoveryOperation(AssetCreationRecoveryStore store, string id, string directory,
        AssetCreationRecoveryManifest? manifest, string[] publicPaths)
    {
        Store = store;
        Id = id;
        Directory = directory;
        Manifest = manifest;
        PublicPaths = publicPaths;
    }

    internal AssetCreationRecoveryStore Store { get; }

    /// <summary>Gets the operation ID. It is also the operation directory name.</summary>
    public string Id { get; }

    /// <summary>Gets the full path of the operation directory.</summary>
    public string Directory { get; }

    /// <summary>Gets the manifest, or null when the manifest is missing or malformed.</summary>
    public AssetCreationRecoveryManifest? Manifest { get; }

    /// <summary>Gets the full public paths that the manifest records.</summary>
    public IReadOnlyList<string> PublicPaths { get; }

    /// <summary>Gets the current state.</summary>
    public EAssetCreationRecoveryState State { get; internal set; }

    /// <summary>Gets the last journal sequence number.</summary>
    public long Sequence { get; internal set; }

    /// <summary>Gets the reason for <see cref="EAssetCreationRecoveryState.RecoveryRequired"/>, or null.</summary>
    public string? Problem { get; internal set; }

    /// <summary>Gets whether this state protects the public paths from automatic asset work.</summary>
    public bool ProtectsPublicPaths => State is EAssetCreationRecoveryState.Preparing
        or EAssetCreationRecoveryState.PublishPending or EAssetCreationRecoveryState.RetainPending
        or EAssetCreationRecoveryState.RestorePending or EAssetCreationRecoveryState.RecoveryRequired;

    internal string OriginalPath(int index) => Path.Combine(Directory, $"original-{index}.bin");
    internal string WorkingPath(int index) => Path.Combine(Directory, $"working-{index}.bin");
    internal string ManifestPath => Path.Combine(Directory, AssetCreationRecoveryStore.ManifestFileName);
    internal string JournalPath => Path.Combine(Directory, AssetCreationRecoveryStore.JournalFileName);
}
