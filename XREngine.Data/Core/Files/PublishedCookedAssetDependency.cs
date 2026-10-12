namespace XREngine.Core.Files;

/// <summary>
/// A separate asset file that a registered cooked serializer references rather than embedding.
/// The path must be a portable game or engine identity also emitted in the serialized payload.
/// </summary>
public readonly record struct PublishedCookedAssetDependency(string AssetPath, Type AssetType);
