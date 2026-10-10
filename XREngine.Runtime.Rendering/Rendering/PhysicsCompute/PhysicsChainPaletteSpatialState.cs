using XREngine.Data.Geometry;

namespace XREngine.Rendering.Compute;

/// <summary>Bounds resident particle translations and the basis used by a palette.</summary>
public readonly record struct PhysicsChainPaletteSpatialState(AABB ParticleBounds, float MaximumBasisStretch);
