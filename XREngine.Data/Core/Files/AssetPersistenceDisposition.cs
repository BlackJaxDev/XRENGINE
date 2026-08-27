namespace XREngine.Core.Files;

/// <summary>
/// Describes whether an asset is an authoring object or a runtime-only projection.
/// </summary>
public enum AssetPersistenceDisposition
{
    Persistent,
    TransientProjection,
}
