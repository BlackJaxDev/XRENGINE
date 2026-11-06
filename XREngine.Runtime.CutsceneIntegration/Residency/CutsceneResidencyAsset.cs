using System.Numerics;

namespace XREngine.Cutscenes;

/// <summary>One asset that a scene node uses, with the world bounds in which it shows and the size of its T0 data.</summary>
/// <param name="AssetId">
/// The asset identifier. The residency backend finds the asset by this value. Use a key that stays the same after a new
/// import, for example a key of <see cref="CutsceneResidencyAssetKeys"/>, because a baked manifest stores it.
/// </param>
/// <param name="Kind">The kind of the asset. It selects the residency backend.</param>
/// <param name="WorldCenter">The center of the bounding sphere in world space.</param>
/// <param name="WorldRadius">The radius of the bounding sphere in world space. It must not be negative.</param>
/// <param name="ProxyBytes">The estimated size of the T0 data of the asset, in bytes.</param>
public readonly record struct CutsceneResidencyAsset(
    Guid AssetId,
    EResidencyAssetKind Kind,
    Vector3 WorldCenter,
    float WorldRadius,
    long ProxyBytes);
