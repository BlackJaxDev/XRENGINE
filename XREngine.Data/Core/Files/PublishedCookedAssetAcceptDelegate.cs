namespace XREngine.Core.Files;

/// <summary>
/// Tells if a registered codec writes one asset now. A codec that declines leaves the asset to the generic cooked
/// serializer, as if its type had no registration.
/// </summary>
public delegate bool PublishedCookedAssetAcceptDelegate(object asset);
