namespace XREngine.Rendering.Compute;

/// <summary>
/// Cumulative outcomes of committed spatial queries and causes of dispatcher spatial epoch
/// advances. Compare two captures over the same window.
/// </summary>
public readonly record struct PhysicsChainCommittedSpatialQueryDiagnostics(
    long Reused,
    long Unfilled,
    long EpochChanged,
    long PublishedPageChanged,
    long RendererChanged,
    long MeshChanged,
    long NoSceneRoute,
    long ScenePublicationChanged,
    long MaterialChanged,
    long EpochAdvancedBySources,
    long EpochAdvancedByRendererState,
    long EpochAdvancedByInvalidation);
