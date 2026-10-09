namespace XREngine.Rendering.Compute;

/// <summary>Describes admission of a committed CPU spatial bound.</summary>
public enum EPhysicsChainSpatialBoundsStatus
{
    Ready,
    NoPublishedOutput,
    IncompleteBoneCoverage,
    MissingBoundsSource,
    UnavailableProducer,
    MissingRendererState,
    BoundsSourceChanged,
    InvalidSpatialBounds,
    MissingMesh,
    MaterialRouteChanged,
    MeshEnvelopeChanged,
    BoneGenerationChanged,
    UnsupportedMaterial,
    MaterialPaddingChanged,
    StaleMaterialSnapshot,
}
