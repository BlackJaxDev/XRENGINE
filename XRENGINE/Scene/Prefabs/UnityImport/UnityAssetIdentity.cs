namespace XREngine.Scene.Prefabs;

/// <summary>
/// Stable identity for an object serialized in, or imported from, a Unity asset.
/// </summary>
[Serializable]
public sealed class UnityAssetIdentity : IEquatable<UnityAssetIdentity>
{
    public string AssetGuid { get; set; } = string.Empty;
    public long LocalFileId { get; set; }
    public UnityAssetObjectKind ObjectKind { get; set; }

    public bool Equals(UnityAssetIdentity? other)
        => other is not null &&
           LocalFileId == other.LocalFileId &&
           ObjectKind == other.ObjectKind &&
           string.Equals(AssetGuid, other.AssetGuid, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj)
        => obj is UnityAssetIdentity other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(AssetGuid ?? string.Empty),
            LocalFileId,
            ObjectKind);

    public override string ToString()
        => $"{AssetGuid}:{LocalFileId}:{ObjectKind}";
}
