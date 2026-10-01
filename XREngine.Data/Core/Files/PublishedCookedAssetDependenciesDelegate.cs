namespace XREngine.Core.Files;

/// <summary>Describes external references according to the serializer that owns the cooked bytes.</summary>
public delegate IReadOnlyList<PublishedCookedAssetDependency> PublishedCookedAssetDependenciesDelegate(object asset);
