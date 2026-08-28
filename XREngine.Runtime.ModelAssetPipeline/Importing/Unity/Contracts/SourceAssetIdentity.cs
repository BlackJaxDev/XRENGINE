namespace XREngine.Scene.Prefabs;

using System.Globalization;
using XREngine.Data.Core;

/// <summary>
/// Stable identity for an object serialized in, or imported from, a Unity asset.
/// </summary>
[Serializable]
public sealed class SourceAssetIdentity : IEquatable<SourceAssetIdentity>
{
    public string AssetGuid { get; set; } = string.Empty;
    public long LocalFileId { get; set; }
    public SourceAssetObjectKind ObjectKind { get; set; }

    public bool Equals(SourceAssetIdentity? other)
        => other is not null &&
           LocalFileId == other.LocalFileId &&
           ObjectKind == other.ObjectKind &&
           string.Equals(AssetGuid, other.AssetGuid, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj)
        => obj is SourceAssetIdentity other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(AssetGuid ?? string.Empty),
            LocalFileId,
            ObjectKind);

    /// <summary>
    /// Converts this Unity source identity into the stable XRENGINE object ID
    /// used by generated native assets.
    /// </summary>
    public Guid ToPersistentID()
        => PersistentObjectID.FromIdentity(
            string.Create(
                CultureInfo.InvariantCulture,
                $"xrengine:unity:{AssetGuid?.Trim().ToLowerInvariant()}:{LocalFileId}:{ObjectKind}"));

    public override string ToString()
        => $"{AssetGuid}:{LocalFileId}:{ObjectKind}";
}
